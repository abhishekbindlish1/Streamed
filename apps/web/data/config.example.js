// Copy to config.local.js. This file is loaded by the browser and TV app:
// keep credentials and API keys on the server, never in this configuration.
window.STREAMED_CONFIG = {
  // Public browser releases enable this to use fixtures without a match backend.
  DEMO_MODE: false,
  INTERNAL_API_BASE_URL: 'https://streamed.pk',
  // Use the included Node server for Team Hub metadata during HTTP development.
  // On a TV, replace this with the reachable URL of your own backend.
  TEAM_HUB_API_BASE_URL: /^https?:$/.test(window.location.protocol) ? window.location.origin : '',
  // Optional: set to your backend URL to enable current standings lookups.
  // With the local Node backend, window.location.origin selects this server.
  TEAM_STANDINGS_API_BASE_URL: ''
};
