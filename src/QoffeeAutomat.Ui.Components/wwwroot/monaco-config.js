// Tells the loader of Monaco where its files are: inside the BlazorMonaco package, served by the agent like every other
// file of the UI. It runs after loader.js and before editor.main.js (see index.html). Nothing is loaded from another host.
(function () {
  'use strict';

  if (window.require && typeof window.require.config === 'function') {
    window.require.config({ paths: { vs: '_content/BlazorMonaco/lib/monaco-editor/min/vs' } });
  }
})();
