const { defineConfig } = require('@playwright/test');

const tizenUserAgent = 'Mozilla/5.0 (SMART-TV; Linux; Tizen 5.5) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/2.2 Chrome/69.0.3497.106 TV Safari/537.36';

module.exports = defineConfig({
  testDir: './tests',
  testMatch: '**/*.spec.js',
  workers: 2,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: 'http://127.0.0.1:3100',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: {
      executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH || undefined
    }
  },
  projects: [
    { name: 'desktop', use: { viewport: { width: 1920, height: 1080 } } },
    { name: 'tizen-1080p', use: { userAgent: tizenUserAgent, viewport: { width: 1920, height: 1080 } } },
    { name: 'tizen-720p', use: { userAgent: tizenUserAgent, viewport: { width: 1280, height: 720 } } }
  ],
  webServer: [
    {
      command: 'node server.js',
      env: { PORT: '3100' },
      url: 'http://127.0.0.1:3100',
      reuseExistingServer: false
    },
    {
      command: 'npm run build && node tests/static-server.js',
      url: 'http://127.0.0.1:3102/Streamed/',
      reuseExistingServer: false
    }
  ]
});
