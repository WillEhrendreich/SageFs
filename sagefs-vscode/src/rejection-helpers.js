// Unhandled-rejection logging that only reports this extension's own failures.
//
// The extension host is one process shared by every extension, and `unhandledRejection` is process-wide, so a
// handler that logs everything also logs another extension's failure under our name (issue #175: Ionide's
// "Check aborted" appeared in the SageFs output channel).
//
// A rejection is ours when its stack runs through this extension's folder. Slashes and case are normalised
// because Windows reports `C:\Users\...` in one place and `c:/users/...` in another. The folder is matched with
// a trailing slash so `sagefs-0.6.9-other` does not count as `sagefs-0.6.9`.

function normalise(p) {
  return String(p).replace(/\\/g, "/").toLowerCase();
}

function isOwnRejection(ownRoot, reason) {
  if (!ownRoot || !reason || typeof reason.stack !== "string") return false;
  const root = normalise(ownRoot).replace(/\/+$/, "") + "/";
  return normalise(reason.stack).includes(root);
}

function installRejectionHandler(ownRoot, log, target) {
  (target || process).on("unhandledRejection", (reason) => {
    if (isOwnRejection(ownRoot, reason)) log("[SageFs] Unhandled rejection: " + reason.stack);
  });
}

module.exports = { isOwnRejection, installRejectionHandler };
