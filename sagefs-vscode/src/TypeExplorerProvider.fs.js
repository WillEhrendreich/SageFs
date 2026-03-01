import { defaultOf, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { Window_createTreeView, newEventEmitter, newThemeIcon, newTreeItem } from "./Vscode.fs.js";
import { truncate, map, tryItem, last } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { substring, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { value as value_2, defaultArg } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { tryField } from "./JsHelpers.fs.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { explore } from "./SageFsClient.fs.js";
import { Record } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { record_type, lambda_type, unit_type, class_type, obj_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";

export let currentClient = createAtom(undefined);

export let refreshEmitter = createAtom(undefined);

export function leafItem(label, desc, icon) {
    const item = newTreeItem(label, 0);
    item.description = desc;
    item.iconPath = newThemeIcon(icon);
    return item;
}

export function expandableItem(label, desc, icon, contextValue) {
    const item = newTreeItem(label, 1);
    item.description = desc;
    item.iconPath = newThemeIcon(icon);
    item.contextValue = contextValue;
    return item;
}

function parseLine(line) {
    let t;
    const trimmed = line.trim();
    if ((t = trimmed, t.startsWith("namespace") ? true : t.startsWith("module"))) {
        const name = last(trimmed.split(" "));
        return expandableItem(name, "", "symbol-namespace", toText(printf("ns:%s"))(name));
    }
    else if (trimmed.startsWith("type")) {
        const t_3 = trimmed;
        return leafItem(defaultArg(tryItem(1, t_3.split(" ")), t_3), "type", "symbol-class");
    }
    else {
        return leafItem(trimmed, "", "symbol-misc");
    }
}

function parseExploreResponse(json) {
    let array;
    try {
        const text = defaultArg(tryField("content", JSON.parse(json)), "");
        return map(parseLine, truncate(50, (array = text.split("\n"), array.filter((l) => (l.trim().length > 0)))));
    }
    catch (matchValue) {
        return undefined;
    }
}

function exploreAndParse(query, errorMsg, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => (explore(query, c).then((_arg) => {
        const result = _arg;
        if (result == null) {
            return Promise.resolve([leafItem("Not connected", "", "warning")]);
        }
        else {
            const matchValue = parseExploreResponse(result);
            if (matchValue == null) {
                return Promise.resolve([leafItem(errorMsg, "", "warning")]);
            }
            else {
                const items = matchValue;
                return Promise.resolve(items);
            }
        }
    }))));
}

export function getChildren(element) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        let c, c$0027;
        const matchValue = currentClient();
        if (element != null) {
            if (matchValue == null) {
                return Promise.resolve([leafItem("Not connected", "", "warning")]);
            }
            else if ((c = matchValue, defaultArg(tryField("contextValue", value_2(element)), "") === "ns-root")) {
                const c_1 = matchValue;
                const el_1 = value_2(element);
                return exploreAndParse("System", "Error parsing response", c_1);
            }
            else {
                const c_2 = matchValue;
                const el_2 = value_2(element);
                const ctx = defaultArg(tryField("contextValue", el_2), "");
                return ((c$0027 = ctx, (c$0027 !== defaultOf()) && c$0027.startsWith("ns:"))) ? (exploreAndParse(substring(ctx, 3), "Error parsing", c_2)) : (Promise.resolve([]));
            }
        }
        else {
            const item = expandableItem("Namespaces", "explore loaded types", "symbol-namespace", "ns-root");
            return Promise.resolve([item]);
        }
    }));
}

export function getTreeItem(element) {
    return element;
}

export class TypeExplorer extends Record {
    constructor(treeView, dispose) {
        super();
        this.treeView = treeView;
        this.dispose = dispose;
    }
}

export function TypeExplorer_$reflection() {
    return record_type("SageFs.Vscode.TypeExplorerProvider.TypeExplorer", [], TypeExplorer, () => [["treeView", class_type("Vscode.TreeView`1", [obj_type])], ["dispose", lambda_type(unit_type, unit_type)]]);
}

export function create(context, c) {
    currentClient(c);
    const emitter = newEventEmitter();
    refreshEmitter(emitter);
    const tv = Window_createTreeView("sagefs-types", {
        treeDataProvider: {
            getTreeItem: getTreeItem,
            getChildren: getChildren,
            onDidChangeTreeData: emitter.event,
        },
    });
    void (context.subscriptions.push(tv));
    return new TypeExplorer(tv, () => {
        tv.dispose();
        emitter.dispose();
    });
}

export function refresh() {
    if (refreshEmitter() == null) {
    }
    else {
        const e = refreshEmitter();
        e.fire(defaultOf());
    }
}

export function setClient(c) {
    currentClient(c);
    refresh();
}

