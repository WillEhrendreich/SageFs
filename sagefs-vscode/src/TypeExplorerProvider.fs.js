import { defaultOf, comparePrimitives, createAtom } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { FSharpSet__Contains, ofSeq } from "./fable_modules/fable-library-js.4.29.0/Set.js";
import { Window_createTreeView, newEventEmitter, newThemeIcon, newTreeItem } from "./Vscode.fs.js";
import { truncate, map } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { value as value_3, defaultArg } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { fieldString } from "./SafeInterop.fs.js";
import { substring, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { exploreCompletions } from "./SageFsClient.fs.js";
import { Record } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { record_type, lambda_type, unit_type, class_type, obj_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";

export let currentClient = createAtom(undefined);

export let refreshEmitter = createAtom(undefined);

export let getSessionId = createAtom(() => undefined);

function kindIcon(kind) {
    switch (kind) {
        case "Namespace":
            return "symbol-namespace";
        case "Module":
            return "symbol-module";
        case "Class":
            return "symbol-class";
        case "Struct":
            return "symbol-struct";
        case "Interface":
            return "symbol-interface";
        case "Enum":
            return "symbol-enum";
        case "Union":
            return "symbol-enum";
        case "Type":
            return "symbol-class";
        case "Method":
        case "OverriddenMethod":
        case "ExtensionMethod":
            return "symbol-method";
        case "Property":
            return "symbol-property";
        case "Field":
            return "symbol-field";
        case "Event":
            return "symbol-event";
        case "Constant":
            return "symbol-constant";
        case "Variable":
            return "symbol-variable";
        case "EnumMember":
            return "symbol-enum-member";
        case "Keyword":
            return "symbol-keyword";
        default:
            return "symbol-misc";
    }
}

const drillableKinds = ofSeq(["Namespace", "Module", "Class", "Struct", "Interface", "Enum", "Union", "Type"], {
    Compare: comparePrimitives,
});

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

function parseCompletionsJson(parentContext, json) {
    try {
        const parsed = JSON.parse(json);
        const completions = ((parsed.completions == null)) ? [] : parsed.completions;
        return (completions.length === 0) ? [leafItem("No members", "", "info")] : map((c) => {
            const label = defaultArg(fieldString("label", c), "?");
            const kind = defaultArg(fieldString("kind", c), "");
            const insertText = defaultArg(fieldString("insertText", c), label);
            const detail = defaultArg(fieldString("detail", c), "");
            const icon = kindIcon(kind);
            const fullName = (parentContext === "") ? insertText : ((parentContext === defaultOf()) ? insertText : toText(printf("%s.%s"))(parentContext)(insertText));
            if (FSharpSet__Contains(drillableKinds, kind)) {
                return expandableItem(label, (kind === "") ? "" : kind, icon, toText(printf("explore:%s"))(fullName));
            }
            else {
                return leafItem(label, (detail === "") ? kind : detail, icon);
            }
        }, truncate(200, completions));
    }
    catch (matchValue_3) {
        return undefined;
    }
}

function exploreAndParse(query, c) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        const sid = defaultArg(getSessionId()(), "");
        return exploreCompletions(query, sid, c).then((_arg) => {
            const result = _arg;
            if (result == null) {
                return Promise.resolve([leafItem("Not connected", "", "warning")]);
            }
            else {
                const matchValue = parseCompletionsJson(query, result);
                if (matchValue == null) {
                    return Promise.resolve([leafItem("Error parsing response", "", "warning")]);
                }
                else {
                    const items = matchValue;
                    return Promise.resolve(items);
                }
            }
        });
    }));
}

const commonRoots = ["System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Text", "Microsoft.FSharp.Collections", "Microsoft.FSharp.Core"];

export function getChildren(element) {
    return PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        let c$0027;
        const matchValue = currentClient();
        if (element != null) {
            if (matchValue == null) {
                return Promise.resolve([leafItem("Not connected", "", "warning")]);
            }
            else {
                const c = matchValue;
                const el = value_3(element);
                const ctx = defaultArg(fieldString("contextValue", el), "");
                return ((c$0027 = ctx, (c$0027 !== defaultOf()) && c$0027.startsWith("explore:"))) ? (exploreAndParse(substring(ctx, 8), c)) : (Promise.resolve([]));
            }
        }
        else {
            const roots = map((ns) => expandableItem(ns, "", "symbol-namespace", toText(printf("explore:%s"))(ns)), commonRoots);
            return Promise.resolve(roots);
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

export function create(context, c, sessionIdFn) {
    currentClient(c);
    getSessionId(sessionIdFn);
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

