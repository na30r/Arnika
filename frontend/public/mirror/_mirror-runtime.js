(() => {
  const runtimeMarker = "__siteMirrorRuntimePatched";
  if (window[runtimeMarker]) {
    return;
  }
  window[runtimeMarker] = true;

  function getMirrorPrefix() {
    const parts = window.location.pathname.split("/");
    if (parts.length < 3 || parts[1] !== "mirror") {
      return null;
    }

    const host = parts[2];
    if (!host) {
      return null;
    }

    return `/mirror/${host}`;
  }

  const mirrorPrefix = getMirrorPrefix();
  if (!mirrorPrefix) {
    return;
  }

  function shouldRewrite(value) {
    if (!value || typeof value !== "string") {
      return false;
    }

    return value.startsWith("/") &&
      !value.startsWith("//") &&
      !value.startsWith("/mirror/") &&
      !value.startsWith("/api/") &&
      !value.startsWith("/_site-mirror");
  }

  function rewritePath(value) {
    if (!shouldRewrite(value)) {
      return value;
    }

    return `${mirrorPrefix}${value}`;
  }

  function tryRewriteUrl(input) {
    if (!input || typeof input !== "string") {
      return input;
    }

    const trimmed = input.trim();
    if (shouldRewrite(trimmed)) {
      return rewritePath(trimmed);
    }

    if (trimmed.startsWith("http://") || trimmed.startsWith("https://")) {
      try {
        const absolute = new URL(trimmed);
        if (absolute.origin === window.location.origin && shouldRewrite(absolute.pathname)) {
          absolute.pathname = rewritePath(absolute.pathname);
          return absolute.toString();
        }
      } catch {
        // Ignore malformed absolute URL and keep original value.
      }
    }

    return input;
  }

  const originalFetch = window.fetch.bind(window);
  window.fetch = (input, init) => {
    if (typeof input === "string") {
      return originalFetch(tryRewriteUrl(input), init);
    }

    if (input instanceof URL) {
      const rewritten = tryRewriteUrl(input.pathname) ?? input.pathname;
      if (rewritten !== input.pathname) {
        const nextUrl = new URL(input.href);
        nextUrl.pathname = rewritten;
        return originalFetch(nextUrl, init);
      }

      return originalFetch(input, init);
    }

    if (input instanceof Request) {
      const rewritten = tryRewriteUrl(input.url);
      if (typeof rewritten === "string" && rewritten !== input.url) {
        const nextRequest = new Request(rewritten, input);
        return originalFetch(nextRequest, init);
      }

      return originalFetch(input, init);
    }

    return originalFetch(input, init);
  };

  const originalOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function patchedOpen(method, url, async, user, password) {
    const rewritten = typeof url === "string" ? tryRewriteUrl(url) : url;
    return originalOpen.call(this, method, rewritten, async, user, password);
  };

  const originalPushState = history.pushState.bind(history);
  history.pushState = function patchedPushState(state, unused, url) {
    if (typeof url === "string") {
      return originalPushState(state, unused, tryRewriteUrl(url));
    }

    return originalPushState(state, unused, url);
  };

  const originalReplaceState = history.replaceState.bind(history);
  history.replaceState = function patchedReplaceState(state, unused, url) {
    if (typeof url === "string") {
      return originalReplaceState(state, unused, tryRewriteUrl(url));
    }

    return originalReplaceState(state, unused, url);
  };
})();
