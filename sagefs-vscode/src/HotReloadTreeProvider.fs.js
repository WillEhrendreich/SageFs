import { Record } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { record_type, array_type, bool_type, string_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { defaultOf, comparePrimitives, stringHash, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { printf, toText, substring, replace } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { Commands_registerCommand, Window_createTreeView, newEventEmitter, newThemeColor, newTreeItem } from "./Vscode.fs.js";
import { item as item_1, map, sortBy } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { Array_groupBy } from "./fable_modules/fable-library-js.4.29.0/Seq2.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { some, value as value_6 } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { watchDirectoryHotReload, unwatchDirectoryHotReload, unwatchAllHotReload, watchAllHotReload, toggleHotReload, getHotReloadState } from "./SageFsClient.fs.js";

export class HotReloadItem extends Record {
    constructor(path, watched, isDirectory, children) {
        super();
        this.path = path;
        this.watched = watched;
        this.isDirectory = isDirectory;
        this.children = children;
    }
}

export function HotReloadItem_$reflection() {
    return record_type("SageFs.Vscode.HotReloadTreeProvider.HotReloadItem", [], HotReloadItem, () => [["path", string_type], ["watched", bool_type], ["isDirectory", bool_type], ["children", array_type(HotReloadItem_$reflection())]]);
}

export let currentClient = createAtom(undefined);

export let currentSessionId = createAtom(undefined);

export let cachedFiles = createAtom([]);

export let refreshEmitter = createAtom(undefined);

export let treeView = createAtom(undefined);

export let autoRefreshTimer = createAtom(undefined);

export function getDirectory(path) {
    if (path == null) {
        return "";
    }
    else {
        const normalized = replace(path, "\\", "/");
        const matchValue = normalized.lastIndexOf("/") | 0;
        if (matchValue === -1) {
            return "";
        }
        else {
            return substring(normalized, 0, matchValue);
        }
    }
}

export function getFileName(path) {
    if (path == null) {
        return "";
    }
    else {
        const normalized = replace(path, "\\", "/");
        const matchValue = normalized.lastIndexOf("/") | 0;
        if (matchValue === -1) {
            return normalized;
        }
        else {
            return substring(normalized, matchValue + 1);
        }
    }
}

export function createDirItem(dirPath, childCount, watchedCount) {
    const item = newTreeItem((dirPath === "") ? "(root)" : dirPath, 2);
    item.contextValue = "directory";
    item.description = toText(printf("%d/%d watched"))(watchedCount)(childCount);
    item.iconPath = newThemeColor("symbolIcon.folderForeground");
    item.command = {
        command: "sagefs.hotReloadToggleDirectory",
        title: "Toggle Directory",
        arguments: [dirPath],
    };
    return item;
}

export function createFileItem(file) {
    const item = newTreeItem(getFileName(file.path), 0);
    item.contextValue = (file.watched ? "watchedFile" : "unwatchedFile");
    item.description = (file.watched ? "● watching" : "○ not watching");
    item.tooltip = file.path;
    item.command = {
        command: "sagefs.hotReloadToggle",
        title: "Toggle Hot Reload",
        arguments: [file.path],
    };
    item.iconPath = (file.watched ? newThemeColor("testing.iconPassed") : newThemeColor("testing.iconSkipped"));
    return item;
}

export function groupByDirectory(files) {
    return sortBy((tuple) => tuple[0], Array_groupBy((f) => getDirectory(f.path), files, {
        Equals: (x, y) => (x === y),
        GetHashCode: stringHash,
    }), {
        Compare: comparePrimitives,
    });
}

export function getChildren(element) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        if (element != null) {
            const el = value_6(element);
            if (el.contextValue === "directory") {
                const label = el.label;
                const dir_1 = (label === "(root)") ? "" : label;
                let files_2;
                const array_4 = cachedFiles();
                files_2 = array_4.filter((f_2) => (getDirectory(f_2.path) === dir_1));
                return Promise.resolve(map(createFileItem, files_2));
            }
            else {
                return Promise.resolve([]);
            }
        }
        else {
            const groups = groupByDirectory(cachedFiles());
            switch (groups.length) {
                case 0: {
                    const item = newTreeItem("No session active", 0);
                    item.description = "Start a session to manage hot reload";
                    return Promise.resolve([item]);
                }
                case 1: {
                    const patternInput = item_1(0, groups);
                    return Promise.resolve(map(createFileItem, patternInput[1]));
                }
                default:
                    return Promise.resolve(map((tupledArg) => {
                        const files_1 = tupledArg[1];
                        let watchedCount;
                        const array_2 = files_1.filter((f_1) => f_1.watched);
                        watchedCount = array_2.length;
                        return createDirItem(tupledArg[0], files_1.length, watchedCount);
                    }, groups));
            }
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
        getChildren: (el) => getChildren((el == null) ? undefined : some(el)),
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
            PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (getHotReloadState(sid, c).then((_arg) => {
                let s;
                const state = _arg;
                return ((state == null) ? ((cachedFiles([]), Promise.resolve())) : ((s = state, (cachedFiles(s.files), Promise.resolve())))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                    if (refreshEmitter() == null) {
                        return Promise.resolve();
                    }
                    else {
                        const e = refreshEmitter();
                        e.fire(defaultOf());
                        return Promise.resolve();
                    }
                }));
            }))));
            break;
        }
        case 1: {
            cachedFiles([]);
            if (refreshEmitter() == null) {
            }
            else {
                const e_1 = refreshEmitter();
                e_1.fire(defaultOf());
            }
            break;
        }
    }
}

export function setSession(c, sessionId) {
    currentClient(c);
    currentSessionId(sessionId);
    if (autoRefreshTimer() == null) {
    }
    else {
        const t = value_6(autoRefreshTimer());
        clearInterval(t);
        autoRefreshTimer(undefined);
    }
    if (sessionId == null) {
    }
    else {
        autoRefreshTimer(some(setInterval((() => {
            refresh();
        }), 5000)));
    }
    refresh();
}

export function register(ctx) {
    const tv = Window_createTreeView("sagefs-hotReload", {
        treeDataProvider: createProvider(),
        showCollapseAll: true,
    });
    treeView(tv);
    void (ctx.subscriptions.push(tv));
    const toggleCmd = Commands_registerCommand("sagefs.hotReloadToggle", (arg) => {
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
                PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (toggleHotReload(sid, arg, c).then((_arg) => {
                    refresh();
                    return Promise.resolve();
                }))));
                break;
            }
            case 1: {
                break;
            }
        }
    });
    void (ctx.subscriptions.push(toggleCmd));
    const watchAllCmd = Commands_registerCommand("sagefs.hotReloadWatchAll", (_arg_1) => {
        const matchValue_3 = currentClient();
        const matchValue_4 = currentSessionId();
        let matchResult_1, c_1, sid_1;
        if (matchValue_3 != null) {
            if (matchValue_4 != null) {
                matchResult_1 = 0;
                c_1 = matchValue_3;
                sid_1 = matchValue_4;
            }
            else {
                matchResult_1 = 1;
            }
        }
        else {
            matchResult_1 = 1;
        }
        switch (matchResult_1) {
            case 0: {
                PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (watchAllHotReload(sid_1, c_1).then((_arg_2) => {
                    refresh();
                    return Promise.resolve();
                }))));
                break;
            }
            case 1: {
                break;
            }
        }
    });
    void (ctx.subscriptions.push(watchAllCmd));
    const unwatchAllCmd = Commands_registerCommand("sagefs.hotReloadUnwatchAll", (_arg_3) => {
        const matchValue_6 = currentClient();
        const matchValue_7 = currentSessionId();
        let matchResult_2, c_2, sid_2;
        if (matchValue_6 != null) {
            if (matchValue_7 != null) {
                matchResult_2 = 0;
                c_2 = matchValue_6;
                sid_2 = matchValue_7;
            }
            else {
                matchResult_2 = 1;
            }
        }
        else {
            matchResult_2 = 1;
        }
        switch (matchResult_2) {
            case 0: {
                PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (unwatchAllHotReload(sid_2, c_2).then((_arg_4) => {
                    refresh();
                    return Promise.resolve();
                }))));
                break;
            }
            case 1: {
                break;
            }
        }
    });
    void (ctx.subscriptions.push(unwatchAllCmd));
    const refreshCmd = Commands_registerCommand("sagefs.hotReloadRefresh", (_arg_5) => {
        refresh();
    });
    void (ctx.subscriptions.push(refreshCmd));
    const toggleDirCmd = Commands_registerCommand("sagefs.hotReloadToggleDirectory", (arg_1) => {
        const matchValue_9 = currentClient();
        const matchValue_10 = currentSessionId();
        let matchResult_3, c_3, sid_3;
        if (matchValue_9 != null) {
            if (matchValue_10 != null) {
                matchResult_3 = 0;
                c_3 = matchValue_9;
                sid_3 = matchValue_10;
            }
            else {
                matchResult_3 = 1;
            }
        }
        else {
            matchResult_3 = 1;
        }
        switch (matchResult_3) {
            case 0: {
                const dir = arg_1;
                let allWatched;
                let array_1;
                const array = cachedFiles();
                array_1 = array.filter((f) => (getDirectory(f.path) === dir));
                allWatched = array_1.every((f_1) => f_1.watched);
                PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => ((allWatched ? (unwatchDirectoryHotReload(sid_3, dir, c_3).then((_arg_6) => {
                    return Promise.resolve();
                })) : (watchDirectoryHotReload(sid_3, dir, c_3).then((_arg_7) => {
                    return Promise.resolve();
                }))).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                    refresh();
                    return Promise.resolve();
                })))));
                break;
            }
            case 1: {
                break;
            }
        }
    });
    void (ctx.subscriptions.push(toggleDirCmd));
}

