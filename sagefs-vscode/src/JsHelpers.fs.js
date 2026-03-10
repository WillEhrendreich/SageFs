import { some } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { createSseSubscriber } from "./sse-helpers.js";

/**
 * DEPRECATED: Use SafeInterop.fieldString/fieldInt/fieldBool/fieldFloat/fieldArray/fieldObj instead.
 * This function uses unbox<'T> which Fable erases to a no-op — no runtime type checking.
 */
export function tryField(name, obj) {
    if ((obj == null)) {
        return undefined;
    }
    else {
        const v = obj[name];
        if ((v == null)) {
            return undefined;
        }
        else {
            return some(v);
        }
    }
}

/**
 * Ignore a promise's result but log rejections to console.error.
 * Prefer promiseIgnoreLog when an output channel is available.
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
 * Ignore a promise's result, logging rejections to the provided sink (e.g. outputChannel.appendLine).
 */
export function promiseIgnoreLog(log, p) {
    let pr_2;
    const pr_1 = p.then((value) => {
    });
    pr_2 = (pr_1.catch((err) => {
        log(toText(printf("[error] Unhandled promise rejection: %O"))(err));
    }));
    void pr_2;
}

/**
 * Simple SSE subscriber: parses `data:` lines as JSON, calls onData(parsed).
 */
export function subscribeSse(url, onData) {
    return createSseSubscriber(url, (_eventType, data) => {
        onData(data);
    }, undefined, undefined, undefined);
}

/**
 * Simple SSE subscriber with optional logger for lifecycle events.
 */
export function subscribeSseWithLogger(url, onData, logger) {
    return createSseSubscriber(url, (_eventType, data) => {
        onData(data);
    }, undefined, logger, undefined);
}

/**
 * Typed SSE subscriber: tracks `event:` type and `data:` payload.
 * Calls onEvent(eventType, parsedData) for each complete SSE message.
 */
export function subscribeTypedSse(url, onEvent) {
    return createSseSubscriber(url, onEvent, undefined, undefined, undefined);
}

/**
 * Typed SSE subscriber with reconnection callback and logger.
 * onReconnect fires when the SSE connection is re-established after a drop.
 * onDisconnect fires immediately when the SSE connection drops (before backoff).
 * logger routes SSE lifecycle messages to the VS Code output channel.
 */
export function subscribeTypedSseWithReconnect(url, onEvent, onReconnect, onDisconnect, logger) {
    return createSseSubscriber(url, onEvent, onReconnect, logger, onDisconnect);
}

