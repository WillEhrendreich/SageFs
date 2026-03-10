import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { getCompletions } from "./SageFsClient.fs.js";
import { map } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { newCompletionItem } from "./Vscode.fs.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { toArray } from "./fable_modules/fable-library-js.4.29.0/Option.js";

export function kindToVscode(kind) {
    switch (kind) {
        case "Method":
            return (0) | 0;
        case "Function":
            return (1) | 0;
        case "Property":
            return (9) | 0;
        case "Field":
            return (4) | 0;
        case "Class":
        case "Type":
            return (6) | 0;
        case "Interface":
            return (7) | 0;
        case "Module":
        case "Namespace":
            return (8) | 0;
        case "Enum":
            return (13) | 0;
        case "Keyword":
            return (14) | 0;
        case "Event":
            return (20) | 0;
        default:
            return (5) | 0;
    }
}

export function create(getClient, getWorkDir) {
    return {
        provideCompletionItems: (doc, pos, _token) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
            const matchValue = getClient();
            if (matchValue != null) {
                const c = matchValue;
                const text = doc.getText();
                const offset = ~~doc.offsetAt(pos) | 0;
                return getCompletions(text, offset, getWorkDir(), c).then((_arg) => (Promise.resolve(map((item) => {
                    const ci = newCompletionItem(item.label, kindToVscode(item.kind));
                    ci.insertText = item.insertText;
                    iterate((d) => {
                        ci.detail = d;
                    }, toArray(item.detail));
                    return ci;
                }, _arg))));
            }
            else {
                return Promise.resolve([]);
            }
        })),
    };
}

