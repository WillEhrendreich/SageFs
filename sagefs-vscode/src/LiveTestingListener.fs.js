import { toArray, orElse, map, defaultArg, some, bind } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { fieldFloat, tryHandleEvent, fieldArray, tryCastArray, fieldString, tryCastInt, tryCastString, fieldObj, fieldInt, duFieldsArray, duFirstFieldString, duCase } from "./SafeInterop.fs.js";
import { parse } from "./fable_modules/fable-library-js.4.29.0/Double.js";
import { fold, map as map_1, tryItem, tryHead, item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { Record, toString } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { VscLiveTestStateModule_summary, VscLiveTestState, VscLiveTestStateModule_update, VscLiveTestStateModule_empty, VscLiveTestState_$reflection, VscFailureNarrative_$reflection, VscTestSummary_$reflection, VscStateChange_$reflection, VscFailureNarrative, VscCausalChange, VscLiveTestEvent, VscResultFreshness, VscTestInfo, VscTestResult, VscTestOutcome, VscTestIdModule_create, VscTestSummary } from "./LiveTestingTypes.fs.js";
import { isEmpty, append, empty, ofArray } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { record_type, float64_type, int32_type, string_type, option_type, array_type, obj_type, lambda_type, unit_type, list_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { processFeatureEvent, FeatureCallbacks, VscTimelineStats_$reflection, VscBindingScopeSnapshot_$reflection, VscCellGraph_$reflection, VscEvalDiff_$reflection, FeatureCallbacks_$reflection } from "./FeatureTypes.fs.js";
import { toConsole, printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { disposeSafe, getEnumerator } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { add } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { subscribeTypedSse, subscribeTypedSseWithReconnect } from "./JsHelpers.fs.js";

/**
 * Extract DU Case string from a Fable-serialized DU object
 */
export function parseDuCase(du) {
    let x;
    return bind(duCase, (x = du, ((x == null)) ? undefined : some(x)));
}

/**
 * Extract the first string field from a Fable-serialized DU's Fields array
 */
export function duFirstFieldStr(du) {
    let x;
    return bind(duFirstFieldString, (x = du, ((x == null)) ? undefined : some(x)));
}

/**
 * Extract DU Fields array from a Fable-serialized DU
 */
export function duFieldsArr(du) {
    let x;
    return bind(duFieldsArray, (x = du, ((x == null)) ? undefined : some(x)));
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
    }, (x = dur, ((x == null)) ? undefined : x));
}

/**
 * Extract TestId string from a server TestId DU object
 */
export function parseTestId(testIdObj) {
    let x, x_1;
    return defaultArg(bind(duFirstFieldString, (x = testIdObj, ((x == null)) ? undefined : some(x))), defaultArg(map(toString, (x_1 = testIdObj, ((x_1 == null)) ? undefined : some(x_1))), ""));
}

/**
 * Map server TestSummary JSON to VscTestSummary
 */
export function parseSummary(data) {
    return new VscTestSummary(defaultArg(fieldInt("Total", data), 0), defaultArg(fieldInt("Passed", data), 0), defaultArg(fieldInt("Failed", data), 0), defaultArg(fieldInt("Running", data), 0), defaultArg(fieldInt("Stale", data), 0), defaultArg(fieldInt("Disabled", data), 0));
}

/**
 * Map a server TestStatusEntry to VscTestResult
 */
export function parseTestResult(entry) {
    let f_2, f_1, f_3;
    const id = VscTestIdModule_create(defaultArg(map(parseTestId, fieldObj("TestId")(entry)), ""));
    const status = defaultArg(fieldObj("Status")(entry), {});
    const statusCase = defaultArg(parseDuCase(status), "Detected");
    const fields = duFieldsArr(status);
    return new VscTestResult(id, (statusCase === "Passed") ? (new VscTestOutcome(0, [])) : ((statusCase === "Failed") ? (new VscTestOutcome(1, [defaultArg(bind(duFirstFieldString, bind((f) => {
        if (f.length === 0) {
            return undefined;
        }
        else {
            return some(item(0, f));
        }
    }, fields)), "test failed")])) : ((statusCase === "Skipped") ? (new VscTestOutcome(2, [defaultArg(bind(tryCastString, bind(tryHead, fields)), "skipped")])) : ((statusCase === "Running") ? (new VscTestOutcome(3, [])) : ((statusCase === "Stale") ? (new VscTestOutcome(5, [])) : ((statusCase === "PolicyDisabled") ? (new VscTestOutcome(6, [])) : (new VscTestOutcome(2, ["unknown status"]))))))), (statusCase === "Passed") ? ((fields != null) ? ((f_2 = fields, bind(parseDuration, bind(tryCastString, tryHead(f_2))))) : undefined) : ((statusCase === "Failed") ? ((fields != null) ? (((f_1 = fields, f_1.length >= 2)) ? ((f_3 = fields, bind(parseDuration, bind(tryCastString, tryItem(1, f_3))))) : undefined) : undefined) : undefined), undefined);
}

/**
 * Map a server TestStatusEntry to VscTestInfo
 */
export function parseTestInfo(entry) {
    const testIdStr = defaultArg(map(parseTestId, fieldObj("TestId")(entry)), "");
    const origin = defaultArg(fieldObj("Origin")(entry), {});
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
            const fields = defaultArg(duFieldsArr(origin), []);
            patternInput = ((fields.length >= 2) ? [bind(tryCastString, tryItem(0, fields)), bind(tryCastInt, tryItem(1, fields))] : [undefined, undefined]);
            break;
        }
        default:
            patternInput = [undefined, undefined];
    }
    return new VscTestInfo(VscTestIdModule_create(testIdStr), defaultArg(fieldString("DisplayName", entry), ""), defaultArg(fieldString("FullName", entry), ""), patternInput[0], patternInput[1]);
}

/**
 * Parse Freshness DU from server JSON (Case/Fields or plain string)
 */
export function parseFreshness(data) {
    const matchValue = bind(parseDuCase, fieldObj("Freshness")(data));
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
        const freshness = parseFreshness(data);
        const entryArray = defaultArg(tryCastArray(entries), []);
        return ofArray([new VscLiveTestEvent(0, [map_1(parseTestInfo, entryArray)]), new VscLiveTestEvent(2, [map_1(parseTestResult, entryArray), freshness])]);
    }, bind((x) => {
        const x_1 = x;
        if ((x_1 == null)) {
            return undefined;
        }
        else {
            return some(x_1);
        }
    }, fieldObj("Entries")(data))), empty());
}

/**
 * Parse a single CausalChange object from server JSON
 */
export function parseCausalChange(obj) {
    return new VscCausalChange(defaultArg(orElse(fieldString("Kind", obj), fieldString("kind", obj)), ""), defaultArg(orElse(fieldString("Name", obj), fieldString("name", obj)), ""));
}

/**
 * Parse a single FailureNarrative from server JSON
 */
export function parseFailureNarrative(obj) {
    const testId = defaultArg(orElse(fieldString("TestId", obj), fieldString("testId", obj)), "");
    const changes = map_1(parseCausalChange, defaultArg(orElse(fieldArray("CausalChanges", obj), fieldArray("causalChanges", obj)), []));
    return new VscFailureNarrative(testId, defaultArg(orElse(fieldString("Summary", obj), fieldString("summary", obj)), ""), defaultArg(orElse(fieldString("TimeSinceLastPass", obj), fieldString("timeSinceLastPass", obj)), ""), changes);
}

/**
 * Parse failure_narratives SSE event (JSON array of narratives)
 */
export function parseFailureNarratives(data) {
    return map_1(parseFailureNarrative, defaultArg(tryCastArray(data), []));
}

export class LiveTestingCallbacks extends Record {
    constructor(OnStateChange, OnSummaryUpdate, OnStatusRefresh, OnBindingsUpdate, OnTestTraceUpdate, OnFeatureEvent, OnEvalResult, OnEvalStarted, OnSourceLocationsUpdate, OnFileAnnotations, OnFailureNarratives, OnWarmupProgress, OnWarmupCompleted, OnFileReloaded, OnSessionFaulted, OnDomainModel, OnDiagnosisReady) {
        super();
        this.OnStateChange = OnStateChange;
        this.OnSummaryUpdate = OnSummaryUpdate;
        this.OnStatusRefresh = OnStatusRefresh;
        this.OnBindingsUpdate = OnBindingsUpdate;
        this.OnTestTraceUpdate = OnTestTraceUpdate;
        this.OnFeatureEvent = OnFeatureEvent;
        this.OnEvalResult = OnEvalResult;
        this.OnEvalStarted = OnEvalStarted;
        this.OnSourceLocationsUpdate = OnSourceLocationsUpdate;
        this.OnFileAnnotations = OnFileAnnotations;
        this.OnFailureNarratives = OnFailureNarratives;
        this.OnWarmupProgress = OnWarmupProgress;
        this.OnWarmupCompleted = OnWarmupCompleted;
        this.OnFileReloaded = OnFileReloaded;
        this.OnSessionFaulted = OnSessionFaulted;
        this.OnDomainModel = OnDomainModel;
        this.OnDiagnosisReady = OnDiagnosisReady;
    }
}

export function LiveTestingCallbacks_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingListener.LiveTestingCallbacks", [], LiveTestingCallbacks, () => [["OnStateChange", lambda_type(list_type(VscStateChange_$reflection()), unit_type)], ["OnSummaryUpdate", lambda_type(VscTestSummary_$reflection(), unit_type)], ["OnStatusRefresh", lambda_type(unit_type, unit_type)], ["OnBindingsUpdate", lambda_type(array_type(obj_type), unit_type)], ["OnTestTraceUpdate", lambda_type(obj_type, unit_type)], ["OnFeatureEvent", option_type(FeatureCallbacks_$reflection())], ["OnEvalResult", lambda_type(string_type, lambda_type(int32_type, lambda_type(string_type, lambda_type(float64_type, unit_type))))], ["OnEvalStarted", lambda_type(string_type, lambda_type(int32_type, unit_type))], ["OnSourceLocationsUpdate", lambda_type(array_type(obj_type), unit_type)], ["OnFileAnnotations", lambda_type(obj_type, unit_type)], ["OnFailureNarratives", lambda_type(array_type(VscFailureNarrative_$reflection()), unit_type)], ["OnWarmupProgress", lambda_type(int32_type, lambda_type(int32_type, lambda_type(string_type, lambda_type(float64_type, lambda_type(string_type, unit_type)))))], ["OnWarmupCompleted", lambda_type(string_type, unit_type)], ["OnFileReloaded", lambda_type(string_type, unit_type)], ["OnSessionFaulted", lambda_type(string_type, unit_type)], ["OnDomainModel", lambda_type(obj_type, unit_type)], ["OnDiagnosisReady", lambda_type(obj_type, unit_type)]]);
}

export class LiveTestingListener extends Record {
    constructor(State, Summary, Bindings, TestTrace, EvalDiff, CellGraph, BindingScope, Timeline, Dispose) {
        super();
        this.State = State;
        this.Summary = Summary;
        this.Bindings = Bindings;
        this.TestTrace = TestTrace;
        this.EvalDiff = EvalDiff;
        this.CellGraph = CellGraph;
        this.BindingScope = BindingScope;
        this.Timeline = Timeline;
        this.Dispose = Dispose;
    }
}

export function LiveTestingListener_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingListener.LiveTestingListener", [], LiveTestingListener, () => [["State", lambda_type(unit_type, VscLiveTestState_$reflection())], ["Summary", lambda_type(unit_type, VscTestSummary_$reflection())], ["Bindings", lambda_type(unit_type, array_type(obj_type))], ["TestTrace", lambda_type(unit_type, option_type(obj_type))], ["EvalDiff", lambda_type(unit_type, option_type(VscEvalDiff_$reflection()))], ["CellGraph", lambda_type(unit_type, option_type(VscCellGraph_$reflection()))], ["BindingScope", lambda_type(unit_type, option_type(VscBindingScopeSnapshot_$reflection()))], ["Timeline", lambda_type(unit_type, option_type(VscTimelineStats_$reflection()))], ["Dispose", lambda_type(unit_type, unit_type)]]);
}

export function start(port, callbacks, onReconnect, onDisconnect, log) {
    let reconnectFn_1, logger, reconnectFn;
    let state = VscLiveTestStateModule_empty;
    let bindings = [];
    let TestTrace = undefined;
    let evalDiff = undefined;
    let cellGraph = undefined;
    let bindingScope = undefined;
    let timeline = undefined;
    const url = toText(printf("http://localhost:%d/events"))(port);
    const featureCallbacks = new FeatureCallbacks((d) => {
        evalDiff = d;
    }, (g) => {
        cellGraph = g;
    }, (s) => {
        bindingScope = s;
    }, (t) => {
        timeline = t;
    });
    const processEvent = (eventType, data) => {
        tryHandleEvent(eventType, () => {
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
                    }, toArray(fieldArray("Bindings", data)));
                    break;
                }
                case "test_trace": {
                    TestTrace = some(data);
                    callbacks.OnTestTraceUpdate(data);
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
                case "eval_started": {
                    const filePath = defaultArg(fieldString("filePath", data), "");
                    if (filePath === "") {
                    }
                    else {
                        callbacks.OnEvalStarted(filePath, defaultArg(fieldInt("blockStartLine", data), 0));
                    }
                    break;
                }
                case "eval_result": {
                    const filePath_1 = defaultArg(fieldString("filePath", data), "");
                    if (filePath_1 === "") {
                    }
                    else {
                        callbacks.OnEvalResult(filePath_1, defaultArg(fieldInt("blockStartLine", data), 0), defaultArg(fieldString("output", data), ""), defaultArg(fieldFloat("durationMs", data), 0));
                    }
                    break;
                }
                case "test_source_locations": {
                    callbacks.OnSourceLocationsUpdate(defaultArg(fieldArray("Locations", data), []));
                    break;
                }
                case "file_annotations": {
                    callbacks.OnFileAnnotations(data);
                    break;
                }
                case "failure_narratives": {
                    const narratives = parseFailureNarratives(data);
                    const narrativeMap = fold((m, n) => add(n.TestId, n, m), state.FailureNarratives, narratives);
                    state = (new VscLiveTestState(state.Tests, state.Results, state.Coverage, state.RunningTests, state.Policies, state.Enabled, state.LastTiming, state.Freshness, narrativeMap));
                    callbacks.OnFailureNarratives(narratives);
                    break;
                }
                case "warmup_progress": {
                    callbacks.OnWarmupProgress(defaultArg(fieldInt("Step", data), 0), defaultArg(fieldInt("Total", data), 0), defaultArg(fieldString("Message", data), ""), defaultArg(fieldFloat("Progress", data), 0), defaultArg(fieldString("Phase", data), ""));
                    break;
                }
                case "warmup_completed": {
                    callbacks.OnWarmupCompleted(defaultArg(fieldString("ProjectName", data), "project"));
                    break;
                }
                case "file_reloaded": {
                    callbacks.OnFileReloaded(defaultArg(orElse(fieldString("FilePath", data), fieldString("filePath", data)), ""));
                    break;
                }
                case "session_faulted": {
                    callbacks.OnSessionFaulted(defaultArg(orElse(fieldString("Reason", data), fieldString("reason", data)), "unknown error"));
                    break;
                }
                case "domain_model": {
                    callbacks.OnDomainModel(data);
                    break;
                }
                case "diagnosis_ready": {
                    callbacks.OnDiagnosisReady(data);
                    break;
                }
                default:
                    undefined;
            }
        });
    };
    let disconnectFn;
    if (onDisconnect == null) {
        disconnectFn = (() => {
        });
    }
    else {
        const fn = onDisconnect;
        disconnectFn = (() => {
            fn();
        });
    }
    const disposable = (onReconnect != null) ? ((log == null) ? ((reconnectFn_1 = onReconnect, subscribeTypedSseWithReconnect(url, processEvent, () => {
        state = VscLiveTestStateModule_empty;
        reconnectFn_1();
    }, disconnectFn, (msg) => {
        try {
            toConsole(printf("[SageFs SSE] %s"))(msg);
        }
        catch (matchValue_2) {
        }
    }))) : ((logger = log, (reconnectFn = onReconnect, subscribeTypedSseWithReconnect(url, processEvent, () => {
        state = VscLiveTestStateModule_empty;
        reconnectFn();
    }, disconnectFn, logger))))) : subscribeTypedSse(url, processEvent);
    return new LiveTestingListener(() => state, () => VscLiveTestStateModule_summary(state), () => bindings, () => TestTrace, () => evalDiff, () => cellGraph, () => bindingScope, () => timeline, () => {
        disposable.dispose();
    });
}

