/// <reference types="vite/client" />

interface ChromeWebView {
  postMessage(message: string): void
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
  removeEventListener(type: 'message', listener: (event: MessageEvent) => void): void
}

interface Chrome {
  webview?: ChromeWebView
}

interface Window {
  chrome?: Chrome
}
