import { toString, Record, Union } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { list_type, string_type, record_type, int32_type, union_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { fieldInt, fieldArray, fieldString, fieldObj } from "./SafeInterop.fs.js";
import { defaultArg, value as value_3 } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { choose, item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { ofArray } from "./fable_modules/fable-library-js.4.29.0/List.js";

export class CoverageHealth extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["AllPassing", "SomeFailing", "NoCoverage"];
    }
}

export function CoverageHealth_$reflection() {
    return union_type("SageFs.Vscode.FileAnnotationsListener.CoverageHealth", [], CoverageHealth, () => [[], [], []]);
}

export class CoverageAnnotation extends Record {
    constructor(Line, Health) {
        super();
        this.Line = (Line | 0);
        this.Health = Health;
    }
}

export function CoverageAnnotation_$reflection() {
    return record_type("SageFs.Vscode.FileAnnotationsListener.CoverageAnnotation", [], CoverageAnnotation, () => [["Line", int32_type], ["Health", CoverageHealth_$reflection()]]);
}

export class InlineFailure extends Record {
    constructor(Line, TestName, Presentation) {
        super();
        this.Line = (Line | 0);
        this.TestName = TestName;
        this.Presentation = Presentation;
    }
}

export function InlineFailure_$reflection() {
    return record_type("SageFs.Vscode.FileAnnotationsListener.InlineFailure", [], InlineFailure, () => [["Line", int32_type], ["TestName", string_type], ["Presentation", string_type]]);
}

export class FileAnnotations extends Record {
    constructor(FilePath, CoverageAnnotations, InlineFailures) {
        super();
        this.FilePath = FilePath;
        this.CoverageAnnotations = CoverageAnnotations;
        this.InlineFailures = InlineFailures;
    }
}

export function FileAnnotations_$reflection() {
    return record_type("SageFs.Vscode.FileAnnotationsListener.FileAnnotations", [], FileAnnotations, () => [["FilePath", string_type], ["CoverageAnnotations", list_type(CoverageAnnotation_$reflection())], ["InlineFailures", list_type(InlineFailure_$reflection())]]);
}

function parseFailurePresentation(f) {
    const matchValue = fieldObj("Failure")(f);
    if (matchValue != null) {
        const failure = value_3(matchValue);
        const case_ = fieldString("Case", failure);
        const fields = defaultArg(fieldArray("Fields", failure), []);
        const matchValue_1 = fields.length | 0;
        let matchResult, n_4, n_5, n_6, n_7;
        if (case_ != null) {
            switch (case_) {
                case "AssertionDiff": {
                    if (matchValue_1 >= 2) {
                        matchResult = 0;
                        n_4 = matchValue_1;
                    }
                    else {
                        matchResult = 4;
                    }
                    break;
                }
                case "ExceptionMessage": {
                    if (matchValue_1 >= 1) {
                        matchResult = 1;
                        n_5 = matchValue_1;
                    }
                    else {
                        matchResult = 4;
                    }
                    break;
                }
                case "Timeout": {
                    if (matchValue_1 >= 1) {
                        matchResult = 2;
                        n_6 = matchValue_1;
                    }
                    else {
                        matchResult = 4;
                    }
                    break;
                }
                case "RawMessage": {
                    if (matchValue_1 >= 1) {
                        matchResult = 3;
                        n_7 = matchValue_1;
                    }
                    else {
                        matchResult = 4;
                    }
                    break;
                }
                default:
                    matchResult = 4;
            }
        }
        else {
            matchResult = 4;
        }
        switch (matchResult) {
            case 0: {
                const arg = toString(item(0, fields));
                const arg_1 = toString(item(1, fields));
                return toText(printf("Expected: %s  Actual: %s"))(arg)(arg_1);
            }
            case 1:
                return toString(item(0, fields));
            case 2: {
                const arg_2 = toString(item(0, fields));
                return toText(printf("Timed out after %s"))(arg_2);
            }
            case 3:
                return toString(item(0, fields));
            default:
                return "";
        }
    }
    else {
        return "";
    }
}

export function parseFileAnnotations(data) {
    const matchValue = fieldString("FilePath", data);
    if (matchValue != null) {
        return new FileAnnotations(matchValue, ofArray(choose((ann) => {
            const line = fieldInt("Line", ann);
            let health;
            const matchValue_1 = fieldString("Health", ann);
            let matchResult;
            if (matchValue_1 != null) {
                switch (matchValue_1) {
                    case "AllPassing": {
                        matchResult = 0;
                        break;
                    }
                    case "SomeFailing": {
                        matchResult = 1;
                        break;
                    }
                    default:
                        matchResult = 2;
                }
            }
            else {
                matchResult = 2;
            }
            switch (matchResult) {
                case 0: {
                    health = (new CoverageHealth(0, []));
                    break;
                }
                case 1: {
                    health = (new CoverageHealth(1, []));
                    break;
                }
                default:
                    health = (new CoverageHealth(2, []));
            }
            if (line == null) {
                return undefined;
            }
            else {
                return new CoverageAnnotation(line, health);
            }
        }, defaultArg(fieldArray("CoverageAnnotations", data), []))), ofArray(choose((f) => {
            const line_1 = fieldInt("Line", f);
            const testName = defaultArg(fieldString("TestName", f), "");
            const presentation = parseFailurePresentation(f);
            if (line_1 == null) {
                return undefined;
            }
            else {
                return new InlineFailure(line_1, testName, presentation);
            }
        }, defaultArg(fieldArray("InlineFailures", data), []))));
    }
    else {
        return undefined;
    }
}

