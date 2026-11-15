package com.jester.client

import android.Manifest
import android.app.Activity
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.window.OnBackInvokedDispatcher
import android.view.Gravity
import android.view.View
import android.webkit.PermissionRequest
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import android.widget.TextView
import java.net.InetSocketAddress
import java.net.Socket

class MainActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var label: TextView
    private var cameraRequest: PermissionRequest? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        web = WebView(this)
        web.setBackgroundColor(Color.parseColor("#0c0c10"))
        web.settings.javaScriptEnabled = true
        web.settings.domStorageEnabled = true
        web.settings.allowFileAccess = false
        web.settings.useWideViewPort = true
        web.settings.loadWithOverviewMode = true
        web.settings.mediaPlaybackRequiresUserGesture = false
        web.webChromeClient = object : WebChromeClient() {
            override fun onPermissionRequest(request: PermissionRequest) {
                val camera = request.resources.contains(PermissionRequest.RESOURCE_VIDEO_CAPTURE)
                if (!camera) {
                    request.deny()
                    return
                }
                if (checkSelfPermission(Manifest.permission.CAMERA) == android.content.pm.PackageManager.PERMISSION_GRANTED) {
                    request.grant(arrayOf(PermissionRequest.RESOURCE_VIDEO_CAPTURE))
                } else {
                    cameraRequest?.deny()
                    cameraRequest = request
                    requestPermissions(arrayOf(Manifest.permission.CAMERA), 2)
                }
            }
        }
        web.settings.cacheMode = android.webkit.WebSettings.LOAD_NO_CACHE
        web.webViewClient = object : WebViewClient() {
            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: android.webkit.WebResourceError) {
                if (!request.isForMainFrame) return
                label.text = "Не удалось открыть интерфейс."
                label.visibility = View.VISIBLE
                web.visibility = View.GONE
            }
        }
        label = TextView(this)
        label.text = "Запуск Jester…"
        label.setTextColor(Color.parseColor("#edf2fa"))
        label.gravity = Gravity.CENTER
        val root = FrameLayout(this)
        root.setBackgroundColor(Color.parseColor("#0c0c10"))
        root.addView(label, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        root.addView(web, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        web.visibility = View.GONE
        setContentView(root)
        if (Build.VERSION.SDK_INT >= 33) {
            onBackInvokedDispatcher.registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT) {
                moveTaskToBack(true)
            }
        }
        Thread {
            val ready = waitForHost()
            runOnUiThread {
                if (ready) {
                    label.visibility = View.GONE
                    web.visibility = View.VISIBLE
                    web.loadUrl("http://127.0.0.1:37841/")
                } else {
                    label.text = "Служба Jester не открыла локальный порт."
                }
            }
        }.start()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != 2) return
        val request = cameraRequest
        cameraRequest = null
        if (request == null) return
        if (grantResults.isNotEmpty() && grantResults[0] == android.content.pm.PackageManager.PERMISSION_GRANTED) {
            request.grant(arrayOf(PermissionRequest.RESOURCE_VIDEO_CAPTURE))
        } else {
            request.deny()
        }
    }

    private fun waitForHost(): Boolean {
        repeat(200) {
            try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress("127.0.0.1", 37841), 200)
                }
                return true
            } catch (_: Exception) {
                Thread.sleep(50)
            }
        }
        return false
    }

    @Deprecated("Back leaves the VPN running and moves the app aside.")
    override fun onBackPressed() {
        moveTaskToBack(true)
    }
}
