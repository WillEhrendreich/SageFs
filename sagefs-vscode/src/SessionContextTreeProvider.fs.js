import { defaultOf, equals, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { Commands_registerCommand, Window_createTreeView, newEventEmitter, newThemeIcon, newTreeItem } from "./Vscode.fs.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { some, defaultArg, value as value_2 } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { fieldString } from "./SafeInterop.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { item as item_2, map, equalsWith } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { promiseIgnore } from "./JsHelpers.fs.js";
import { getWarmupContext } from "./SageFsClient.fs.js";

export let currentClient = createAtom(undefined);

export let currentSessionId = createAtom(undefined);

export let cachedContext = createAtom(undefined);

export let refreshEmitter = createAtom(undefined);

export let autoRefreshTimer = createAtom(undefined);

export let isLoading = createAtom(false);

export function sectionItem(label, desc, icon) {
    const item = newTreeItem(label, 2);
    item.description = desc;
    item.iconPath = newThemeIcon(icon);
    item.contextValue = "section";
    return item;
}

export function leafItem(label, desc, icon) {
    const item = newTreeItem(label, 0);
    item.description = desc;
    item.iconPath = newThemeIcon(icon);
    return item;
}

export function summaryItem(ctx) {
    const nsCount = ctx.NamespacesOpened.length | 0;
    const failCount = ctx.FailedOpens.length | 0;
    let desc;
    const arg = ctx.AssembliesLoaded.length | 0;
    desc = toText(printf("%d assemblies | %d namespaces | %dms"))(arg)(nsCount)(ctx.WarmupDurationMs);
    const item = newTreeItem("Session Context", 2);
    item.description = desc;
    item.iconPath = newThemeIcon("symbol-event");
    item.contextValue = "summary";
    if (failCount === 0) {
    }
    else {
        item.description = toText(printf("%s | %d failed"))(desc)(failCount);
    }
    return item;
}

export function getChildren(element) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        let matchValue, arg;
        if (element != null) {
            const el = value_2(element);
            const ctx_1 = defaultArg(fieldString("contextValue", el), "");
            switch (ctx_1) {
                case "summary":
                    if (cachedContext() != null) {
                        const wc = cachedContext();
                        const sections = [];
                        return ((matchValue = wc.AssembliesLoaded, (!equalsWith(equals, matchValue, defaultOf()) && (matchValue.length === 0)) ? (Promise.resolve()) : ((void (sections.push(sectionItem("Assemblies", (arg = (matchValue.length | 0), toText(printf("%d loaded"))(arg)), "package"))), Promise.resolve())))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            let matchValue_1, arg_1;
                            return ((matchValue_1 = wc.NamespacesOpened, (!equalsWith(equals, matchValue_1, defaultOf()) && (matchValue_1.length === 0)) ? (Promise.resolve()) : ((void (sections.push(sectionItem("Namespaces", (arg_1 = (matchValue_1.length | 0), toText(printf("%d opened"))(arg_1)), "symbol-namespace"))), Promise.resolve())))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                let matchValue_2, arg_2;
                                return ((matchValue_2 = wc.FailedOpens, (!equalsWith((x_2, y_2) => equalsWith((x_3, y_3) => (x_3 === y_3), x_2, y_2), matchValue_2, defaultOf()) && (matchValue_2.length === 0)) ? (Promise.resolve()) : ((void (sections.push(sectionItem("Failed Opens", (arg_2 = (matchValue_2.length | 0), toText(printf("%d failed"))(arg_2)), "error"))), Promise.resolve())))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => (Promise.resolve(sections.slice()))));
                            }));
                        }));
                    }
                    else {
                        return Promise.resolve([]);
                    }
                case "section": {
                    const label = defaultArg(fieldString("label", el), "");
                    if (cachedContext() != null) {
                        const wc_1 = cachedContext();
                        return (label === "Assemblies") ? (Promise.resolve(map((a) => leafItem(a.Name, toText(printf("%d ns, %d mod"))(a.NamespaceCount)(a.ModuleCount), "library"), wc_1.AssembliesLoaded))) : ((label === "Namespaces") ? (Promise.resolve(map((b) => {
                            const kind = b.IsModule ? "module" : "namespace";
                            return leafItem(b.Name, toText(printf("%s via %s"))(kind)(b.Source), "symbol-namespace");
                        }, wc_1.NamespacesOpened))) : ((label === "Failed Opens") ? (Promise.resolve(map((pair) => leafItem((pair.length === 0) ? "?" : item_2(0, pair), (pair.length > 1) ? item_2(1, pair) : "unknown", "error"), wc_1.FailedOpens))) : (Promise.resolve([]))));
                    }
                    else {
                        return Promise.resolve([]);
                    }
                }
                default:
                    return Promise.resolve([]);
            }
        }
        else if (isLoading()) {
            const item = newTreeItem("$(loading~spin) Loading...", 0);
            return Promise.resolve([item]);
        }
        else if (cachedContext() != null) {
            const ctx = cachedContext();
            return Promise.resolve([summaryItem(ctx)]);
        }
        else {
            const item_1 = newTreeItem("No session context", 0);
            item_1.description = "Waiting for session...";
            return Promise.resolve([item_1]);
        }
    }));
}

export function getTreeItem(element) {
    return element;
}

export function createProvider() {
    const emitter = newEventEmitter();
    refreshEmitter(emitter);
    return {
        onDidChangeTreeData: emitter.event,
        getChildren: (el) => {
            let x;
            return getChildren((x = el, ((x == null)) ? undefined : some(x)));
        },
        getTreeItem: getTreeItem,
    };
}

export function refresh() {
    const matchValue = currentClient();
    const matchValue_1 = currentSessionId();
    let matchResult, c, sid;
    if (matchValue != null) {
        if (matchValue_1 != null) {
            matchResult = 0;
            c = matchValue;
            sid = matchValue_1;
        }
        else {
            matchResult = 1;
        }
    }
    else {
        matchResult = 1;
    }
    switch (matchResult) {
        case 0: {
            isLoading(true);
            if (refreshEmitter() == null) {
            }
            else {
                const e = refreshEmitter();
                e.fire(defaultOf());
            }
            promiseIgnore(PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getWarmupContext(sid, c).then((_arg) => {
                cachedContext(_arg);
                isLoading(false);
                if (refreshEmitter() == null) {
                    return Promise.resolve();
                }
                else {
                    const e_1 = refreshEmitter();
                    e_1.fire(defaultOf());
                    return Promise.resolve();
                }
            })))));
            break;
        }
        case 1: {
            isLoading(false);
            cachedContext(undefined);
            if (refreshEmitter() == null) {
            }
            else {
                const e_2 = refreshEmitter();
                e_2.fire(defaultOf());
            }
            break;
        }
    }
}

export function setSession(c, sessionId) {
    if (equals(currentSessionId(), sessionId)) {
        currentClient(c);
    }
    else {
        currentClient(c);
        currentSessionId(sessionId);
        if (autoRefreshTimer() == null) {
        }
        else {
            const t = value_2(autoRefreshTimer());
            clearInterval(t);
            autoRefreshTimer(undefined);
        }
        if (sessionId == null) {
        }
        else {
            autoRefreshTimer(some(setInterval((() => {
                refresh();
            }), 30000)));
        }
        refresh();
    }
}

export function stopAutoRefresh() {
    if (autoRefreshTimer() == null) {
    }
    else {
        const t = value_2(autoRefreshTimer());
        clearInterval(t);
        autoRefreshTimer(undefined);
    }
}

export function register(ctx) {
    const tv = Window_createTreeView("sagefs-sessionContext", {
        treeDataProvider: createProvider(),
        showCollapseAll: true,
    });
    void (ctx.subscriptions.push(tv));
    const refreshCmd = Commands_registerCommand("sagefs.sessionContextRefresh", (_arg) => {
        refresh();
    });
    void (ctx.subscriptions.push(refreshCmd));
}

