package com.jester.client

import android.app.Application
import android.content.Intent
import android.os.ParcelFileDescriptor
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

object JesterVpn {
    @Volatile
    var app: Application? = null

    private val gate = Any()
    private val generation = AtomicInteger(0)
    private val consentOk = AtomicBoolean(false)
    private val openedFd = AtomicInteger(-1)
    @Volatile private var consent: CountDownLatch? = null
    @Volatile private var opened: CountDownLatch? = null
    @Volatile private var ready: CountDownLatch? = null

    @JvmStatic
    fun establish(): Int {
        val ctx = app ?: return -1
        val gen = generation.incrementAndGet()
        synchronized(gate) {
            val boot = CountDownLatch(1)
            ready = boot
            try {
                ctx.startForegroundService(serviceIntent(JesterVpnService.ACTION_PREPARE, gen))
            } catch (_: Exception) {
                return -1
            }
            if (!boot.await(8, TimeUnit.SECONDS)) {
                stop()
                return -1
            }
            if (android.net.VpnService.prepare(ctx) != null) {
                val latch = CountDownLatch(1)
                consent = latch
                consentOk.set(false)
                try {
                    ctx.startActivity(Intent(ctx, VpnConsentActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                } catch (_: Exception) {
                    stop()
                    return -2
                }
                if (!latch.await(90, TimeUnit.SECONDS) || !consentOk.get()) {
                    stop()
                    return -2
                }
            }
            val latch = CountDownLatch(1)
            opened = latch
            openedFd.set(-1)
            try {
                ctx.startForegroundService(serviceIntent(JesterVpnService.ACTION_OPEN, gen))
            } catch (_: Exception) {
                stop()
                return -1
            }
            if (!latch.await(20, TimeUnit.SECONDS)) {
                stop()
                return -1
            }
            val fd = openedFd.get()
            if (fd < 0) stop()
            return fd
        }
    }

    @JvmStatic
    fun stop() {
        generation.incrementAndGet()
        val ctx = app ?: return
        ctx.stopService(Intent(ctx, JesterVpnService::class.java))
    }

    fun signalReady() {
        ready?.countDown()
    }

    fun finishConsent(ok: Boolean) {
        consentOk.set(ok)
        consent?.countDown()
    }

    fun deliver(gen: Int, fd: Int) {
        if (gen != generation.get()) {
            closeFd(fd)
            return
        }
        openedFd.set(fd)
        opened?.countDown()
    }

    private fun serviceIntent(action: String, gen: Int): Intent {
        val ctx = app ?: error("JesterVpn.app is not set")
        return Intent(ctx, JesterVpnService::class.java)
            .setAction(action)
            .putExtra(JesterVpnService.EXTRA_GEN, gen)
    }

    private fun closeFd(fd: Int) {
        if (fd < 0) return
        try {
            ParcelFileDescriptor.adoptFd(fd).close()
        } catch (_: Exception) {
        }
    }
}
