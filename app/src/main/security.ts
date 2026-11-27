import { app, session, shell, type Session, type WebContents } from "electron";

/**
 * App-wide hardening, applied before any window exists:
 * - every renderer is sandboxed (contextIsolation on, nodeIntegration off,
 *   set per window as well);
 * - no renderer may open windows: https links go to the user's browser;
 * - no <webview> tags;
 * - top-level navigation is limited per web contents by `allowNavigation`;
 * - permission requests are denied, except fullscreen for the player.
 */
export function hardenApp(): void {
  app.enableSandbox();

  app.on("web-contents-created", (_event, contents) => {
    contents.setWindowOpenHandler(({ url }) => {
      openExternal(url);
      return { action: "deny" };
    });
    contents.on("will-attach-webview", (event) => event.preventDefault());
  });
}

/** Lets `contents` navigate only to URLs that `allowed` accepts; anything else opens externally if it is https. */
export function allowNavigation(contents: WebContents, allowed: (url: URL) => boolean): void {
  const guard = (event: { preventDefault(): void; url: string }): void => {
    let url: URL;
    try {
      url = new URL(event.url);
    } catch {
      event.preventDefault();
      return;
    }

    if (!allowed(url)) {
      event.preventDefault();
      openExternal(event.url);
    }
  };
  contents.on("will-navigate", guard);
  contents.on("will-redirect", guard);
}

export function lockPermissions(target: Session, allowFullscreen: (contents: WebContents | null) => boolean): void {
  target.setPermissionRequestHandler((contents, permission, callback) => callback(permission === "fullscreen" && allowFullscreen(contents)));
  target.setPermissionCheckHandler((contents, permission) => permission === "fullscreen" && allowFullscreen(contents));
  // Nothing in the shell downloads files.
  target.on("will-download", (event) => event.preventDefault());
}

export function hardenSessions(viewer: Session, isViewer: (contents: WebContents | null) => boolean): void {
  lockPermissions(session.defaultSession, () => false);
  lockPermissions(viewer, isViewer);
}

function openExternal(url: string): void {
  try {
    if (new URL(url).protocol === "https:") {
      void shell.openExternal(url);
    }
  } catch {
    // Not a URL: ignore.
  }
}
