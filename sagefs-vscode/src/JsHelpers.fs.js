import { some } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { createSseSubscriber } from "./sse-helpers.js";

/**
 * Null-safe field extraction from a JS object
 */
export function tryField(name, obj) {
    const v = obj[name];
    if (v == null) {
        return undefined;
    }
    else {
        return some(v);
    }
}

/**
 * Ignore a promise's result but log rejections instead of swallowing them silently.
 */
export function promiseIgnore(p) {
    let pr_2;
    const pr_1 = p.then((value) => {
    });
    pr_2 = (pr_1.catch((err) => {
        console.error('[SageFs] unhandled promise rejection:', err);
    }));
    void pr_2;
}

/**
 * Simple SSE subscriber: parses `data:` lines as JSON, calls onData(parsed).
 */
export function subscribeSse(url, onData) {
    return createSseSubscriber(url, (_eventType, data) => {
        onData(data);
    });
}

/**
 * Typed SSE subscriber: tracks `event:` type and `data:` payload.
 * Calls onEvent(eventType, parsedData) for each complete SSE message.
 */
export function subscribeTypedSse(url, onEvent) {
    return createSseSubscriber(url, onEvent);
}

