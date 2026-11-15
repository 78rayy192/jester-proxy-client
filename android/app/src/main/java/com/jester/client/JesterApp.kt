package com.jester.client

import android.app.Application
import android.content.res.AssetManager
import android.os.Build
import android.system.Os
import java.io.File

class JesterApp : Application() {
    override fun onCreate() {
        super.onCreate()
        JesterVpn.app = this
        val ui = File(filesDir, "ui")
        extractUi(ui)
        Os.setenv("JESTER_HOME", filesDir.absolutePath, true)
        Os.setenv("JESTER_UI", ui.absolutePath, true)
        Os.setenv("HOME", filesDir.absolutePath, true)
        Os.setenv("XDG_DATA_HOME", filesDir.absolutePath, true)
        val bundled = File(applicationInfo.nativeLibraryDir, "libmihomo.so")
        if (bundled.isFile) Os.setenv("JESTER_MIHOMO", bundled.absolutePath, true)
        System.loadLibrary("jester")
    }

    private fun extractUi(dest: File) {
        val stamp = File(dest, ".stamp")
        val code = "${versionCode()}:${lastUpdateTime()}"
        if (stamp.isFile && stamp.readText() == code && File(dest, "index.html").isFile) return
        dest.deleteRecursively()
        copyAssets(assets, "ui", dest)
        stamp.writeText(code)
    }

    @Suppress("DEPRECATION")
    private fun versionCode(): Long {
        val info = packageManager.getPackageInfo(packageName, 0)
        return if (Build.VERSION.SDK_INT >= 28) info.longVersionCode else info.versionCode.toLong()
    }

    @Suppress("DEPRECATION")
    private fun lastUpdateTime(): Long {
        return packageManager.getPackageInfo(packageName, 0).lastUpdateTime
    }

    private fun copyAssets(assets: AssetManager, path: String, dest: File) {
        val children = assets.list(path).orEmpty()
        if (children.isEmpty()) {
            dest.parentFile?.mkdirs()
            assets.open(path).use { input -> dest.outputStream().use { input.copyTo(it) } }
            return
        }
        dest.mkdirs()
        for (name in children) copyAssets(assets, "$path/$name", File(dest, name))
    }
}
