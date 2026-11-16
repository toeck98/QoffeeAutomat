// Browser helpers of QoffeeAutomat.Ui.Components. Loaded as an ES module by JsBrowserInterop; it makes no network
// request and loads nothing from another host.

const visibilityHandlers = new Map();
let nextHandlerId = 1;

export function isPageVisible() {
  return document.visibilityState !== 'hidden';
}

export function subscribeVisibility(callback) {
  const id = nextHandlerId++;
  const handler = () => {
    callback.invokeMethodAsync('OnChanged', document.visibilityState !== 'hidden').catch(() => {
      // The component is gone; the unsubscribe call is on its way.
    });
  };
  document.addEventListener('visibilitychange', handler);
  visibilityHandlers.set(id, handler);
  return id;
}

export function unsubscribeVisibility(id) {
  const handler = visibilityHandlers.get(id);
  if (handler) {
    document.removeEventListener('visibilitychange', handler);
    visibilityHandlers.delete(id);
  }
}

export function getStored(key) {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    // The clipboard API needs a secure context and a permission; the old way works without.
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    let copied = false;
    try {
      copied = document.execCommand('copy');
    } catch {
      copied = false;
    }
    document.body.removeChild(area);
    return copied;
  }
}

export function setStored(key, value) {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Storage can be blocked; the setting then lasts until the page is closed.
  }
}

// Offers bytes as a file download. The object URL is local and released at once.
export function downloadFile(fileName, contentType, bytes) {
  const blob = new Blob([bytes], { type: contentType });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.style.display = 'none';
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
