package com.jester.client

import android.app.Activity
import android.content.Intent
import android.net.VpnService
import android.os.Bundle

class VpnConsentActivity : Activity() {
    private var reported = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (savedInstanceState != null) return
        val prepare = VpnService.prepare(this)
        if (prepare == null) {
            report(true)
            finish()
            return
        }
        @Suppress("DEPRECATION")
        startActivityForResult(prepare, REQUEST)
    }

    @Deprecated("Vpn consent still reports through onActivityResult")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        if (requestCode == REQUEST) {
            report(resultCode == RESULT_OK)
            finish()
        }
    }

    override fun onDestroy() {
        report(false)
        super.onDestroy()
    }

    private fun report(ok: Boolean) {
        if (reported) return
        reported = true
        JesterVpn.finishConsent(ok)
    }

    companion object {
        private const val REQUEST = 1
    }
}
