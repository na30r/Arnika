"use client";

import { useEffect, useId } from "react";

type Props = {
  headHtml: string;
};

/**
 * Inserts the mirrored document's <head> nodes into the real `document.head` so
 * <link rel="stylesheet"> and <style> load/apply. React does not run these when
 * placed in a body div. Cleanup runs on unmount or when headHtml changes.
 */
export function MirrorHeadInjector({ headHtml }: Props) {
  const id = useId().replace(/:/g, "");

  useEffect(() => {
    if (!headHtml) {
      return;
    }

    const parser = new DOMParser();
    const parsed = parser.parseFromString(
      `<!doctype html><html><head>${headHtml}</head><body></body></html>`,
      "text/html"
    );
    const fromHead = Array.from(parsed.head.childNodes);
    const injected: ChildNode[] = [];

    for (const node of fromHead) {
      const n = document.importNode(node, true);
      if (n.nodeType === Node.ELEMENT_NODE) {
        (n as Element).setAttribute("data-mirror-injected", id);
      }
      document.head.appendChild(n);
      injected.push(n);
    }

    return () => {
      for (const n of injected) {
        n.parentNode?.removeChild(n);
      }
    };
  }, [headHtml, id]);

  return null;
}
