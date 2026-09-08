/// <reference types="vite/client" />

interface ChromeWebView {
  postMessage(message: string): void
}

interface Chrome {
  webview?: ChromeWebView
}

interface Window {
  chrome?: Chrome
}
