import { PromiseBuilder__For_1565554B, PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { rangeDouble } from "./fable_modules/fable-library-js.4.29.0/Range.js";
import { map, item as item_1 } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { getCompletions } from "./SageFsClient.fs.js";
import { newCompletionItem } from "./Vscode.fs.js";

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
                const lines = text.split("\n");
                let offset = 0;
                return PromiseBuilder__For_1565554B(promise, rangeDouble(0, 1, ~~pos.line - 1), (_arg) => {
                    offset = (((offset + item_1(_arg, lines).length) + 1) | 0);
                    return Promise.resolve();
                }).then(() => PromiseBuilder__Delay_62FBFDE1(promise, () => {
                    offset = ((offset + ~~pos.character) | 0);
                    return getCompletions(text, offset, getWorkDir(), c).then((_arg_1) => (Promise.resolve(map((item) => {
                        const ci = newCompletionItem(item.label, kindToVscode(item.kind));
                        ci.insertText = item.insertText;
                        return ci;
                    }, _arg_1))));
                }));
            }
            else {
                return Promise.resolve([]);
            }
        })),
    };
}

