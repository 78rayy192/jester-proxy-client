package com.jester.client

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import org.json.JSONObject
import java.io.File
import kotlin.concurrent.thread

class JesterVpnService : VpnService() {
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val action = intent?.action
        val gen = intent?.getIntExtra(EXTRA_GEN, 0) ?: 0
        if (action == ACTION_STOP) {
            hide()
            stopSelf()
            return START_NOT_STICKY
        }
        promote(connecting = action != ACTION_OPEN)
        if (action == ACTION_PREPARE) {
            JesterVpn.signalReady()
            return START_NOT_STICKY
        }
        if (action != ACTION_OPEN) return START_NOT_STICKY
        thread(name = "jester-tun") {
            val fd = try {
                openTun()
            } catch (_: Exception) {
                -1
            }
            JesterVpn.deliver(gen, fd)
            if (fd < 0) {
                hide()
                stopSelf()
            } else {
                promote(connecting = false)
            }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        hide()
        super.onDestroy()
    }

    private fun openTun(): Int {
        val spec = readSpec()
        val builder = Builder()
            .setSession("Jester")
            .addAddress(spec.address, spec.prefix)
            .addRoute("0.0.0.0", 0)
            .addDnsServer(spec.dns)
            .setMtu(spec.mtu)
            .setBlocking(false)
            .setConfigureIntent(
                PendingIntent.getActivity(
                    this,
                    0,
                    Intent(this, MainActivity::class.java),
                    PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
                ),
            )
        try {
            builder.addAddress("fdfe:dcba:9876::1", 126)
            builder.addRoute("::", 0)
        } catch (_: Exception) {
        }
        if (Build.VERSION.SDK_INT >= 29) builder.setMetered(false)
        try {
            builder.addDisallowedApplication(packageName)
        } catch (_: Exception) {
        }
        val pfd: ParcelFileDescriptor = try {
            builder.establish()
        } catch (_: Exception) {
            null
        } ?: return -1
        return pfd.detachFd()
    }

    private fun readSpec(): Spec {
        val file = File(filesDir, "run/vpn.json")
        return try {
            val json = JSONObject(file.readText())
            Spec(
                json.optString("address", "172.19.0.1"),
                json.optInt("prefix", 30).coerceIn(1, 32),
                json.optString("dns", "172.19.0.2"),
                json.optInt("mtu", 1400).coerceIn(1280, 9000),
            )
        } catch (_: Exception) {
            Spec("172.19.0.1", 30, "172.19.0.2", 1400)
        }
    }

    private fun promote(connecting: Boolean) {
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL) == null) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "VPN", NotificationManager.IMPORTANCE_LOW))
        }
        val open = PendingIntent.getActivity(
            this,
            1,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val notification = Notification.Builder(this, CHANNEL)
            .setContentTitle("Jester")
            .setContentText(if (connecting) "Подключение VPN…" else "VPN включён")
            .setSmallIcon(android.R.drawable.ic_lock_lock)
            .setOngoing(true)
            .setContentIntent(open)
            .build()
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIFICATION, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFICATION, notification)
        }
    }

    private fun hide() {
        try {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } catch (_: Exception) {
        }
    }

    private data class Spec(val address: String, val prefix: Int, val dns: String, val mtu: Int)

    companion object {
        const val ACTION_PREPARE = "com.jester.client.VPN_PREPARE"
        const val ACTION_OPEN = "com.jester.client.VPN_OPEN"
        const val ACTION_STOP = "com.jester.client.VPN_STOP"
        const val EXTRA_GEN = "gen"
        private const val CHANNEL = "jester-vpn"
        private const val NOTIFICATION = 1
    }
}
