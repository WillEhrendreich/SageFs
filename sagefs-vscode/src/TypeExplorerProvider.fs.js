import { defaultOf, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { Window_createTreeView, newEventEmitter, newThemeIcon, newTreeItem } from "./Vscode.fs.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { defaultArg, value as value_4 } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { explore } from "./SageFsClient.fs.js";
import { tryItem, last, map, truncate } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { substring, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
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

export function getChildren(element) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        let el, c;
        const matchValue = currentClient();
        if (element != null) {
            if (matchValue == null) {
                return Promise.resolve([leafItem("Not connected", "", "warning")]);
            }
            else if ((el = value_4(element), (c = matchValue, el.contextValue === "ns-root"))) {
                const c_1 = matchValue;
                const el_1 = value_4(element);
                return explore("System", c_1).then((_arg) => {
                    const result = _arg;
                    if (result == null) {
                        return Promise.resolve([leafItem("Not connected", "", "warning")]);
                    }
                    else {
                        const json = result;
                        return PromiseBuilder__Delay_62FBFDE1(promise, () => {
                            let array;
                            const parsed = JSON.parse(json);
                            const text = parsed.content;
                            const lines = truncate(50, (array = text.split("\n"), array.filter((l) => (l.trim().length > 0))));
                            return Promise.resolve(map((line) => {
                                const trimmed = line.trim();
                                if (trimmed.startsWith("namespace") ? true : trimmed.startsWith("module")) {
                                    const name = last(trimmed.split(" "));
                                    return expandableItem(name, "", "symbol-namespace", toText(printf("ns:%s"))(name));
                                }
                                else if (trimmed.startsWith("type")) {
                                    return leafItem(defaultArg(tryItem(1, trimmed.split(" ")), trimmed), "type", "symbol-class");
                                }
                                else {
                                    return leafItem(trimmed, "", "symbol-misc");
                                }
                            }, lines));
                        }).catch((_arg_1) => (Promise.resolve([leafItem("Error parsing response", "", "warning")])));
                    }
                });
            }
            else {
                const c_2 = matchValue;
                const el_2 = value_4(element);
                const ctx = el_2.contextValue;
                if ((ctx !== defaultOf()) && ctx.startsWith("ns:")) {
                    const nsName = substring(ctx, 3);
                    return explore(nsName, c_2).then((_arg_2) => {
                        const result_1 = _arg_2;
                        if (result_1 == null) {
                            return Promise.resolve([leafItem("Could not explore", "", "warning")]);
                        }
                        else {
                            const json_1 = result_1;
                            return PromiseBuilder__Delay_62FBFDE1(promise, () => {
                                let array_5;
                                const parsed_1 = JSON.parse(json_1);
                                const text_1 = parsed_1.content;
                                const lines_1 = truncate(50, (array_5 = text_1.split("\n"), array_5.filter((l_1) => (l_1.trim().length > 0))));
                                return Promise.resolve(map((line_1) => {
                                    const trimmed_1 = line_1.trim();
                                    if (trimmed_1.startsWith("namespace") ? true : trimmed_1.startsWith("module")) {
                                        const name_2 = last(trimmed_1.split(" "));
                                        return expandableItem(name_2, "", "symbol-namespace", toText(printf("ns:%s"))(name_2));
                                    }
                                    else if (trimmed_1.startsWith("type")) {
                                        return leafItem(defaultArg(tryItem(1, trimmed_1.split(" ")), trimmed_1), "type", "symbol-class");
                                    }
                                    else {
                                        return leafItem(trimmed_1, "", "symbol-misc");
                                    }
                                }, lines_1));
                            }).catch((_arg_3) => (Promise.resolve([leafItem("Error parsing", "", "warning")])));
                        }
                    });
                }
                else {
                    return Promise.resolve([]);
                }
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

