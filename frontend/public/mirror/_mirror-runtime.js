(() => {
  const runtimeMarker = "__siteMirrorRuntimePatched";
  if (window[runtimeMarker]) {
    return;
  }
  window[runtimeMarker] = true;

  function getMirrorContext() {
    const parts = window.location.pathname.split("/");
    if (parts.length < 4 || parts[1] !== "mirror") {
      return null;
    }

    const host = parts[2];
    const version = parts[3];
    if (!host || !version) {
      return null;
    }

    let basePrefix = `/mirror/${host}/${version}`;
    let contentPrefix = basePrefix;
    if (parts.length >= 6 && parts[4] === "_localized") {
      contentPrefix = `${basePrefix}/_localized/${parts[5]}`;
    }

    return {
      host,
      basePrefix,
      contentPrefix
    };
  }

  const mirrorContext = getMirrorContext();
  if (!mirrorContext) {
    return;
  }
  const { host, basePrefix, contentPrefix } = mirrorContext;
  const normalizedHost = host.toLowerCase();

  function getRemoteFallbackUrl(value) {
    if (!value || typeof value !== "string") {
      return null;
    }

    const trimmed = value.trim();
    if (!trimmed) {
      return null;
    }

    if (trimmed.startsWith("http://") || trimmed.startsWith("https://")) {
      if (isMirroredAbsoluteUrl(trimmed)) {
        return trimmed;
      }

      return null;
    }

    if (!trimmed.startsWith("/")) {
      return null;
    }

    if (trimmed.startsWith("//") || trimmed.startsWith("/mirror/")) {
      return null;
    }

    const queryIndex = trimmed.indexOf("?");
    const hashIndex = trimmed.indexOf("#");
    let endIndex = trimmed.length;
    if (queryIndex >= 0) {
      endIndex = Math.min(endIndex, queryIndex);
    }
    if (hashIndex >= 0) {
      endIndex = Math.min(endIndex, hashIndex);
    }

    const pathOnly = trimmed.slice(0, endIndex);
    const suffix = trimmed.slice(endIndex);

    const basePathPrefix = `${basePrefix}/`;
    const contentPathPrefix = `${contentPrefix}/`;
    let upstreamPath = pathOnly;
    if (upstreamPath.startsWith(basePathPrefix)) {
      upstreamPath = `/${upstreamPath.slice(basePathPrefix.length)}`;
    } else if (upstreamPath.startsWith(contentPathPrefix)) {
      upstreamPath = `/${upstreamPath.slice(contentPathPrefix.length)}`;
    }

    if (!upstreamPath.startsWith("/")) {
      upstreamPath = `/${upstreamPath}`;
    }

    return `https://${host}${upstreamPath}${suffix}`;
  }
  const remoteOrigin = `https://${host}`;

  function shouldRewrite(value) {
    if (!value || typeof value !== "string") {
      return false;
    }

    return value.startsWith("/") &&
      !value.startsWith("//") &&
      !value.startsWith("/mirror/") &&
      !value.startsWith("/_site-mirror");
  }

  function rewritePath(value) {
    if (!shouldRewrite(value)) {
      return value;
    }

    return `${basePrefix}${value}`;
  }

  function getRemoteFallbackUrl(value) {
    try {
      const parsed = new URL(value, window.location.origin);
      if (parsed.origin !== window.location.origin) {
        return null;
      }

      const basePrefixWithSlash = `${basePrefix}/`;
      if (!parsed.pathname.startsWith(basePrefixWithSlash)) {
        return null;
      }

      const strippedPath = parsed.pathname.slice(basePrefix.length);
      return `${remoteOrigin}${strippedPath}${parsed.search}${parsed.hash}`;
    } catch {
      return null;
    }
  }

  function isMirroredAbsoluteUrl(value) {
    try {
      const parsed = new URL(value);
      if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
        return false;
      }

      if (parsed.origin === window.location.origin && shouldRewrite(parsed.pathname)) {
        return true;
      }

      const requestHost = parsed.hostname.toLowerCase();
      return requestHost === normalizedHost ||
        requestHost === `www.${normalizedHost}` ||
        `www.${requestHost}` === normalizedHost;
    } catch {
      return false;
    }
  }

  function rewriteNavigationPath(value) {
    if (!value || typeof value !== "string") {
      return value;
    }

    if (value.startsWith("/") &&
      !value.startsWith("//") &&
      !value.startsWith("/mirror/")) {
      return `${contentPrefix}${value}`;
    }

    if (isMirroredAbsoluteUrl(value)) {
      const parsed = new URL(value);
      if (shouldRewrite(parsed.pathname)) {
        return `${contentPrefix}${parsed.pathname}${parsed.search}${parsed.hash}`;
      }

      return value;
    }

    return value;
  }

  function rewriteSrcSet(value) {
    if (!value || typeof value !== "string") {
      return value;
    }

    const candidates = value
      .split(",")
      .map((item) => item.trim())
      .filter(Boolean);
    if (candidates.length === 0) {
      return value;
    }

    const rewritten = candidates.map((candidate) => {
      const separatorIndex = candidate.search(/\s/);
      if (separatorIndex < 0) {
        return tryRewriteUrl(candidate);
      }

      const url = candidate.slice(0, separatorIndex);
      const descriptor = candidate.slice(separatorIndex).trimStart();
      const nextUrl = tryRewriteUrl(url);
      return descriptor ? `${nextUrl} ${descriptor}` : nextUrl;
    });

    return rewritten.join(", ");
  }

  function rewriteElementAttributes(element) {
    if (!(element instanceof Element)) {
      return;
    }

    const tagName = element.tagName.toLowerCase();
    for (const attribute of Array.from(element.attributes)) {
      const name = attribute.name.toLowerCase();
      const value = attribute.value;
      if (!value) {
        continue;
      }

      if (name === "srcset" || name === "imagesrcset") {
        const nextValue = rewriteSrcSet(value);
        if (nextValue !== value) {
          element.setAttribute(attribute.name, nextValue);
        }
        continue;
      }

      if (name === "href" && (tagName === "a" || tagName === "area")) {
        const nextValue = rewriteNavigationPath(value);
        if (nextValue !== value) {
          element.setAttribute(attribute.name, nextValue);
        }
        continue;
      }

      if (name === "src" ||
        name === "href" ||
        name === "action" ||
        name === "formaction" ||
        name === "poster" ||
        name === "data") {
        const nextValue = tryRewriteUrl(value);
        if (nextValue !== value) {
          element.setAttribute(attribute.name, nextValue);
        }
      }
    }

    attachResourceFallback(element);
  }

  function attachResourceFallback(element) {
    if (!(element instanceof Element)) {
      return;
    }

    const tagName = element.tagName.toLowerCase();
    if (tagName !== "script" && tagName !== "link") {
      return;
    }

    const source =
      tagName === "script"
        ? element.getAttribute("src")
        : element.getAttribute("href");
    if (!source) {
      return;
    }

    const fallbackUrl = getRemoteFallbackUrl(source);
    if (!fallbackUrl) {
      return;
    }

    if (element.getAttribute("data-site-mirror-fallback-attached") === "1") {
      return;
    }

    element.setAttribute("data-site-mirror-fallback-attached", "1");
    element.addEventListener("error", () => {
      if (element.getAttribute("data-site-mirror-fallback-used") === "1") {
        return;
      }

      element.setAttribute("data-site-mirror-fallback-used", "1");
      if (tagName === "script") {
        element.setAttribute("src", fallbackUrl);
      } else {
        element.setAttribute("href", fallbackUrl);
      }
    });
  }

  function tryRewriteUrl(input) {
    if (!input || typeof input !== "string") {
      return input;
    }

    const trimmed = input.trim();
    if (shouldRewrite(trimmed)) {
      return rewritePath(trimmed);
    }

    if (isMirroredAbsoluteUrl(trimmed)) {
      try {
        const absolute = new URL(trimmed);
        if (shouldRewrite(absolute.pathname)) {
          return `${basePrefix}${absolute.pathname}${absolute.search}${absolute.hash}`;
        }
      } catch {
        // Ignore malformed absolute URL and keep original value.
      }
    }

    return input;
  }

  const originalFetch = window.fetch.bind(window);
  window.fetch = async (input, init) => {
    let rewrittenInput = input;
    let fallbackUrl = null;

    if (typeof input === "string") {
      rewrittenInput = tryRewriteUrl(input);
      fallbackUrl = getRemoteFallbackUrl(rewrittenInput);
    } else if (input instanceof URL) {
      const rewritten = tryRewriteUrl(input.pathname) ?? input.pathname;
      if (rewritten !== input.pathname) {
        const nextUrl = new URL(input.href);
        nextUrl.pathname = rewritten;
        rewrittenInput = nextUrl;
        fallbackUrl = getRemoteFallbackUrl(nextUrl.toString());
      }
    } else if (input instanceof Request) {
      const rewritten = tryRewriteUrl(input.url);
      if (typeof rewritten === "string" && rewritten !== input.url) {
        rewrittenInput = new Request(rewritten, input);
        fallbackUrl = getRemoteFallbackUrl(rewritten);
      }
    }

    let response = await originalFetch(rewrittenInput, init);
    const requestMethod = (init?.method || (input instanceof Request ? input.method : "GET")).toUpperCase();
    if (fallbackUrl && requestMethod === "GET" && response.status === 404) {
      response = await originalFetch(fallbackUrl, init);
    }

    return response;
  };

  const originalOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function patchedOpen(method, url, async, user, password) {
    const rewritten = typeof url === "string" ? tryRewriteUrl(url) : url;
    return originalOpen.call(this, method, rewritten, async, user, password);
  };

  const originalPushState = history.pushState.bind(history);
  history.pushState = function patchedPushState(state, unused, url) {
    if (typeof url === "string") {
      return originalPushState(state, unused, rewriteNavigationPath(url));
    }

    return originalPushState(state, unused, url);
  };

  const originalReplaceState = history.replaceState.bind(history);
  history.replaceState = function patchedReplaceState(state, unused, url) {
    if (typeof url === "string") {
      return originalReplaceState(state, unused, rewriteNavigationPath(url));
    }

    return originalReplaceState(state, unused, url);
  };

  const originalSetAttribute = Element.prototype.setAttribute;
  Element.prototype.setAttribute = function patchedSetAttribute(name, value) {
    const normalizedName = String(name).toLowerCase();
    const rawValue = typeof value === "string" ? value : String(value);

    if (normalizedName === "srcset" || normalizedName === "imagesrcset") {
      const rewritten = rewriteSrcSet(rawValue);
      return originalSetAttribute.call(this, name, rewritten);
    }

    if (normalizedName === "href" &&
      (this.tagName?.toLowerCase() === "a" || this.tagName?.toLowerCase() === "area")) {
      const rewritten = rewriteNavigationPath(rawValue);
      return originalSetAttribute.call(this, name, rewritten);
    }

    if (normalizedName === "src" ||
      normalizedName === "href" ||
      normalizedName === "action" ||
      normalizedName === "formaction" ||
      normalizedName === "poster" ||
      normalizedName === "data") {
      const rewritten = tryRewriteUrl(rawValue);
      const result = originalSetAttribute.call(this, name, rewritten);
      attachResourceFallback(this);
      return result;
    }

    const result = originalSetAttribute.call(this, name, value);
    attachResourceFallback(this);
    return result;
  };

  const originalAppendChild = Node.prototype.appendChild;
  Node.prototype.appendChild = function patchedAppendChild(node) {
    if (node instanceof Element) {
      rewriteElementAttributes(node);
      for (const child of node.querySelectorAll("*")) {
        rewriteElementAttributes(child);
      }
    }

    return originalAppendChild.call(this, node);
  };

  const originalInsertBefore = Node.prototype.insertBefore;
  Node.prototype.insertBefore = function patchedInsertBefore(newNode, referenceNode) {
    if (newNode instanceof Element) {
      rewriteElementAttributes(newNode);
      for (const child of newNode.querySelectorAll("*")) {
        rewriteElementAttributes(child);
      }
    }

    return originalInsertBefore.call(this, newNode, referenceNode);
  };
})();
