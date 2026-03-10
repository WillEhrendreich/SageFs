import { equals, defaultOf, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { map, last, choose, equalsWith } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { printf, toText, split, join } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { Commands_registerCommand, Window_createTreeView, newEventEmitter, newThemeIcon, newTreeItem } from "./Vscode.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { some } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { promiseIgnore } from "./JsHelpers.fs.js";
import { listSessions } from "./SageFsClient.fs.js";

export let currentClient = createAtom(undefined);

export let cachedSessions = createAtom([]);

export let activeId = createAtom(undefined);

export let refreshEmitter = createAtom(undefined);

function stripExt(name) {
    if (name.endsWith(".fsproj")) {
        const n_3 = name;
        return n_3.slice(undefined, (n_3.length - 8) + 1);
    }
    else if (name.endsWith(".slnx")) {
        const n_4 = name;
        return n_4.slice(undefined, (n_4.length - 6) + 1);
    }
    else if (name.endsWith(".sln")) {
        const n_5 = name;
        return n_5.slice(undefined, (n_5.length - 5) + 1);
    }
    else {
        return name;
    }
}

function projectLabel(s) {
    const matchValue = s.projects;
    if (!equalsWith((x, y) => (x === y), matchValue, defaultOf()) && (matchValue.length === 0)) {
        return "no project";
    }
    else {
        return join(", ", choose((p) => {
            if ((p == null)) {
                return undefined;
            }
            else {
                return stripExt(last(split(p, ["/", "\\"])));
            }
        }, matchValue));
    }
}

function statusIcon(status) {
    switch (status) {
        case "Ready":
        case "Evaluating":
            return "$(zap)";
        case "Starting":
        case "Restarting":
            return "$(loading~spin)";
        case "Faulted":
            return "$(error)";
        case "Stopped":
            return "$(circle-slash)";
        default:
            return "$(question)";
    }
}

export function getChildren(_element) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        if (!equalsWith(equals, cachedSessions(), defaultOf()) && (cachedSessions().length === 0)) {
            const item = newTreeItem("No sessions", 0);
            item.description = "Create one with $(add) above";
            item.iconPath = newThemeIcon("info");
            return Promise.resolve([item]);
        }
        else {
            const sessions = cachedSessions();
            return Promise.resolve(map((s) => {
                let arg, arg_1, matchValue, matchValue_1, arg_6;
                const isActive = (activeId() == null) ? false : (activeId() === s.id);
                const item_1 = newTreeItem((arg = statusIcon(s.status), (arg_1 = projectLabel(s), toText(printf("%s %s"))(arg)(arg_1))), 0);
                item_1.description = ((matchValue = (s.evalCount | 0), (matchValue === 0) ? s.status : toText(printf("%s [%d evals]"))(s.status)(matchValue)));
                item_1.iconPath = (isActive ? newThemeIcon("star-full") : newThemeIcon("terminal"));
                item_1.contextValue = ((matchValue_1 = s.status, isActive ? ((matchValue_1 === "Ready") ? "session-active-ready" : "session-active") : ((matchValue_1 === "Stopped") ? "session-stopped" : "session-inactive")));
                item_1.tooltip = ((arg_6 = projectLabel(s), toText(printf("ID: %s\nStatus: %s\nProject: %s\nEvals: %d"))(s.id)(s.status)(arg_6)(s.evalCount)));
                item_1.sessionId = s.id;
                return item_1;
            }, sessions));
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
    if (currentClient() == null) {
    }
    else {
        const c = currentClient();
        promiseIgnore(PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (listSessions(c).then((_arg) => {
            cachedSessions(_arg);
            if (refreshEmitter() == null) {
                return Promise.resolve();
            }
            else {
                const e = refreshEmitter();
                e.fire(defaultOf());
                return Promise.resolve();
            }
        })))));
    }
}

export function setSession(c, sessionId) {
    currentClient(c);
    activeId(sessionId);
    refresh();
}

export function register(ctx) {
    const tv = Window_createTreeView("sagefs-sessions", {
        treeDataProvider: createProvider(),
        showCollapseAll: false,
    });
    void (ctx.subscriptions.push(tv));
    const refreshCmd = Commands_registerCommand("sagefs.sessionsRefresh", (_arg) => {
        refresh();
    });
    void (ctx.subscriptions.push(refreshCmd));
}

