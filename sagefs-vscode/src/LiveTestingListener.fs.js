import { toArray, bind, some, defaultArg, map } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { Record, toString } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { map as map_1, tryItem, tryHead, item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { parse } from "./fable_modules/fable-library-js.4.29.0/Double.js";
import { subscribeTypedSse, tryField } from "./JsHelpers.fs.js";
import { VscLiveTestStateModule_summary, VscLiveTestStateModule_update, VscLiveTestStateModule_empty, VscLiveTestState_$reflection, VscTestSummary_$reflection, VscStateChange_$reflection, VscLiveTestEvent, VscResultFreshness, VscTestInfo, VscTestResult, VscTestOutcome, VscTestIdModule_create, VscTestSummary } from "./LiveTestingTypes.fs.js";
import { isEmpty, append, empty, ofArray } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { record_type, option_type, array_type, obj_type, lambda_type, unit_type, list_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { processFeatureEvent, FeatureCallbacks, VscTimelineStats_$reflection, VscBindingScopeSnapshot_$reflection, VscCellGraph_$reflection, VscEvalDiff_$reflection, FeatureCallbacks_$reflection } from "./FeatureTypes.fs.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { disposeSafe, getEnumerator } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";

/**
 * Extract DU Case string from a Fable-serialized DU object
 */
export function parseDuCase(du) {
    let x;
    return map((du_1) => {
        let x_2;
        return defaultArg(map((value) => value, (x_2 = du_1.Case, (x_2 == null) ? undefined : some(x_2))), toString(du_1));
    }, (x = du, (x == null) ? undefined : some(x)));
}

/**
 * Extract the first field from a Fable-serialized DU's Fields array
 */
export function duFirstField(du) {
    let x;
    return bind((du_1) => {
        let x_1;
        return bind((fields) => {
            const arr = fields;
            if (arr.length === 0) {
                return undefined;
            }
            else {
                return some(item(0, arr));
            }
        }, (x_1 = du_1.Fields, (x_1 == null) ? undefined : some(x_1)));
    }, (x = du, (x == null) ? undefined : some(x)));
}

/**
 * Extract DU Fields array from a Fable-serialized DU
 */
export function duFields(du) {
    let x;
    return bind((du_1) => {
        let x_1;
        return map((value) => value, (x_1 = du_1.Fields, (x_1 == null) ? undefined : some(x_1)));
    }, (x = du, (x == null) ? undefined : some(x)));
}

/**
 * Parse HH:MM:SS duration string to milliseconds
 */
export function parseDuration(dur) {
    let x;
    return bind((dur_1) => {
        const parts = dur_1.split(":");
        if (parts.length === 3) {
            return (((parse(item(0, parts)) * 3600) + (parse(item(1, parts)) * 60)) + parse(item(2, parts))) * 1000;
        }
        else {
            return undefined;
        }
    }, (x = dur, (x == null) ? undefined : x));
}

/**
 * Extract TestId string from a server TestId DU object
 */
export function parseTestId(testIdObj) {
    let x, x_1;
    return defaultArg(bind(duFirstField, (x = testIdObj, (x == null) ? undefined : some(x))), defaultArg(map(toString, (x_1 = testIdObj, (x_1 == null) ? undefined : some(x_1))), ""));
}

/**
 * Map server TestSummary JSON to VscTestSummary
 */
export function parseSummary(data) {
    return new VscTestSummary(defaultArg(tryField("Total", data), 0), defaultArg(tryField("Passed", data), 0), defaultArg(tryField("Failed", data), 0), defaultArg(tryField("Running", data), 0), defaultArg(tryField("Stale", data), 0), defaultArg(tryField("Disabled", data), 0));
}

/**
 * Map a server TestStatusEntry to VscTestResult
 */
export function parseTestResult(entry) {
    let f_2, f_1, f_3;
    const id = VscTestIdModule_create(defaultArg(map(parseTestId, tryField("TestId", entry)), ""));
    const status = defaultArg(tryField("Status", entry), {});
    const statusCase = defaultArg(parseDuCase(status), "Detected");
    const fields = duFields(status);
    return new VscTestResult(id, (statusCase === "Passed") ? (new VscTestOutcome(0, [])) : ((statusCase === "Failed") ? (new VscTestOutcome(1, [defaultArg(bind(duFirstField, bind((f) => {
        if (f.length === 0) {
            return undefined;
        }
        else {
            return some(item(0, f));
        }
    }, fields)), "test failed")])) : ((statusCase === "Skipped") ? (new VscTestOutcome(2, [defaultArg(bind((x) => {
        const x_1 = x;
        if (x_1 == null) {
            return undefined;
        }
        else {
            return x_1;
        }
    }, bind(tryHead, fields)), "skipped")])) : ((statusCase === "Running") ? (new VscTestOutcome(3, [])) : ((statusCase === "Stale") ? (new VscTestOutcome(5, [])) : ((statusCase === "PolicyDisabled") ? (new VscTestOutcome(6, [])) : (new VscTestOutcome(2, ["unknown status"]))))))), (statusCase === "Passed") ? ((fields != null) ? ((f_2 = fields, bind(parseDuration, bind((x_2) => {
        const x_3 = x_2;
        if (x_3 == null) {
            return undefined;
        }
        else {
            return x_3;
        }
    }, tryHead(f_2))))) : undefined) : ((statusCase === "Failed") ? ((fields != null) ? (((f_1 = fields, f_1.length >= 2)) ? ((f_3 = fields, bind(parseDuration, bind((x_4) => {
        const x_5 = x_4;
        if (x_5 == null) {
            return undefined;
        }
        else {
            return x_5;
        }
    }, tryItem(1, f_3))))) : undefined) : undefined) : undefined), undefined);
}

/**
 * Map a server TestStatusEntry to VscTestInfo
 */
export function parseTestInfo(entry) {
    const testIdStr = defaultArg(map(parseTestId, tryField("TestId", entry)), "");
    const origin = defaultArg(tryField("Origin", entry), {});
    let patternInput;
    const matchValue = parseDuCase(origin);
    let matchResult;
    if (matchValue != null) {
        if (matchValue === "SourceMapped") {
            matchResult = 0;
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
            const fields = defaultArg(duFields(origin), []);
            patternInput = ((fields.length >= 2) ? [bind((x) => {
                const x_1 = x;
                if (x_1 == null) {
                    return undefined;
                }
                else {
                    return x_1;
                }
            }, tryItem(0, fields)), bind((x_2) => {
                const x_3 = x_2 | 0;
                if (x_3 == null) {
                    return undefined;
                }
                else {
                    return x_3;
                }
            }, tryItem(1, fields))] : [undefined, undefined]);
            break;
        }
        default:
            patternInput = [undefined, undefined];
    }
    return new VscTestInfo(VscTestIdModule_create(testIdStr), defaultArg(tryField("DisplayName", entry), ""), defaultArg(tryField("FullName", entry), ""), patternInput[0], patternInput[1]);
}

/**
 * Parse Freshness DU from server JSON (Case/Fields or plain string)
 */
export function parseFreshness(data) {
    const matchValue = bind(parseDuCase, tryField("Freshness", data));
    let matchResult;
    if (matchValue != null) {
        switch (matchValue) {
            case "StaleCodeEdited": {
                matchResult = 0;
                break;
            }
            case "StaleWrongGeneration": {
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
        case 0:
            return new VscResultFreshness(1, []);
        case 1:
            return new VscResultFreshness(2, []);
        default:
            return new VscResultFreshness(0, []);
    }
}

/**
 * Parse test_results_batch → VscLiveTestEvent pair (discovery + results)
 */
export function parseResultsBatch(data) {
    return defaultArg(map((entries) => {
        let x_2;
        const freshness = parseFreshness(data);
        const entryArray = defaultArg(map((value) => value, (x_2 = entries, (x_2 == null) ? undefined : some(x_2))), []);
        return ofArray([new VscLiveTestEvent(0, [map_1(parseTestInfo, entryArray)]), new VscLiveTestEvent(2, [map_1(parseTestResult, entryArray), freshness])]);
    }, bind((x) => {
        const x_1 = x;
        if (x_1 == null) {
            return undefined;
        }
        else {
            return some(x_1);
        }
    }, tryField("Entries", data))), empty());
}

export class LiveTestingCallbacks extends Record {
    constructor(OnStateChange, OnSummaryUpdate, OnStatusRefresh, OnBindingsUpdate, OnPipelineTraceUpdate, OnFeatureEvent) {
        super();
        this.OnStateChange = OnStateChange;
        this.OnSummaryUpdate = OnSummaryUpdate;
        this.OnStatusRefresh = OnStatusRefresh;
        this.OnBindingsUpdate = OnBindingsUpdate;
        this.OnPipelineTraceUpdate = OnPipelineTraceUpdate;
        this.OnFeatureEvent = OnFeatureEvent;
    }
}

export function LiveTestingCallbacks_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingListener.LiveTestingCallbacks", [], LiveTestingCallbacks, () => [["OnStateChange", lambda_type(list_type(VscStateChange_$reflection()), unit_type)], ["OnSummaryUpdate", lambda_type(VscTestSummary_$reflection(), unit_type)], ["OnStatusRefresh", lambda_type(unit_type, unit_type)], ["OnBindingsUpdate", lambda_type(array_type(obj_type), unit_type)], ["OnPipelineTraceUpdate", lambda_type(obj_type, unit_type)], ["OnFeatureEvent", option_type(FeatureCallbacks_$reflection())]]);
}

export class LiveTestingListener extends Record {
    constructor(State, Summary, Bindings, PipelineTrace, EvalDiff, CellGraph, BindingScope, Timeline, Dispose) {
        super();
        this.State = State;
        this.Summary = Summary;
        this.Bindings = Bindings;
        this.PipelineTrace = PipelineTrace;
        this.EvalDiff = EvalDiff;
        this.CellGraph = CellGraph;
        this.BindingScope = BindingScope;
        this.Timeline = Timeline;
        this.Dispose = Dispose;
    }
}

export function LiveTestingListener_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingListener.LiveTestingListener", [], LiveTestingListener, () => [["State", lambda_type(unit_type, VscLiveTestState_$reflection())], ["Summary", lambda_type(unit_type, VscTestSummary_$reflection())], ["Bindings", lambda_type(unit_type, array_type(obj_type))], ["PipelineTrace", lambda_type(unit_type, option_type(obj_type))], ["EvalDiff", lambda_type(unit_type, option_type(VscEvalDiff_$reflection()))], ["CellGraph", lambda_type(unit_type, option_type(VscCellGraph_$reflection()))], ["BindingScope", lambda_type(unit_type, option_type(VscBindingScopeSnapshot_$reflection()))], ["Timeline", lambda_type(unit_type, option_type(VscTimelineStats_$reflection()))], ["Dispose", lambda_type(unit_type, unit_type)]]);
}

export function start(port, callbacks) {
    let state = VscLiveTestStateModule_empty;
    let bindings = [];
    let pipelineTrace = undefined;
    let evalDiff = undefined;
    let cellGraph = undefined;
    let bindingScope = undefined;
    let timeline = undefined;
    const featureCallbacks = new FeatureCallbacks((d) => {
        evalDiff = d;
    }, (g) => {
        cellGraph = g;
    }, (s) => {
        bindingScope = s;
    }, (t) => {
        timeline = t;
    });
    const disposable = subscribeTypedSse(toText(printf("http://localhost:%d/events"))(port), (eventType, data) => {
        let matchValue, custom;
        switch (eventType) {
            case "test_summary": {
                callbacks.OnSummaryUpdate(parseSummary(data));
                break;
            }
            case "test_results_batch": {
                let allChanges = empty();
                const enumerator = getEnumerator(parseResultsBatch(data));
                try {
                    while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
                        const patternInput = VscLiveTestStateModule_update(enumerator["System.Collections.Generic.IEnumerator`1.get_Current"](), state);
                        state = patternInput[0];
                        allChanges = append(allChanges, patternInput[1]);
                    }
                }
                finally {
                    disposeSafe(enumerator);
                }
                if (!isEmpty(allChanges)) {
                    callbacks.OnStateChange(allChanges);
                }
                break;
            }
            case "state": {
                callbacks.OnStatusRefresh();
                break;
            }
            case "session": {
                break;
            }
            case "bindings_snapshot": {
                iterate((arr) => {
                    bindings = arr;
                    callbacks.OnBindingsUpdate(bindings);
                }, toArray(tryField("Bindings", data)));
                break;
            }
            case "pipeline_trace": {
                pipelineTrace = some(data);
                callbacks.OnPipelineTraceUpdate(data);
                break;
            }
            case "eval_diff":
            case "cell_dependencies":
            case "binding_scope_map":
            case "eval_timeline": {
                processFeatureEvent(eventType, data, (matchValue = callbacks.OnFeatureEvent, (matchValue == null) ? featureCallbacks : ((custom = matchValue, new FeatureCallbacks((d_1) => {
                    featureCallbacks.OnEvalDiff(d_1);
                    custom.OnEvalDiff(d_1);
                }, (g_1) => {
                    featureCallbacks.OnCellGraph(g_1);
                    custom.OnCellGraph(g_1);
                }, (s_1) => {
                    featureCallbacks.OnBindingScope(s_1);
                    custom.OnBindingScope(s_1);
                }, (t_1) => {
                    featureCallbacks.OnTimeline(t_1);
                    custom.OnTimeline(t_1);
                })))));
                break;
            }
            default:
                undefined;
        }
    });
    return new LiveTestingListener(() => state, () => VscLiveTestStateModule_summary(state), () => bindings, () => pipelineTrace, () => evalDiff, () => cellGraph, () => bindingScope, () => timeline, () => {
        disposable.dispose();
    });
}

