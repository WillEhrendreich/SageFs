import { Record, Union } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { tuple_type, array_type, class_type, float64_type, record_type, int32_type, option_type, union_type, string_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { tryFind, toList as toList_1, filter, count, iterate, FSharpMap__get_Count, add, empty } from "./fable_modules/fable-library-js.4.29.0/Map.js";
import { equals, comparePrimitives, compare } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { FSharpSet__get_Count, difference, ofArray, empty as empty_1 } from "./fable_modules/fable-library-js.4.29.0/Set.js";
import { map, fold } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { choose, singleton } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { empty as empty_2, singleton as singleton_1, append, delay, toList } from "./fable_modules/fable-library-js.4.29.0/Seq.js";

export class VscTestOutcome extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Passed", "Failed", "Skipped", "Running", "Errored", "Stale", "PolicyDisabled"];
    }
}

export function VscTestOutcome_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscTestOutcome", [], VscTestOutcome, () => [[], [["message", string_type]], [["reason", string_type]], [], [["message", string_type]], [], []]);
}

export class VscTestId extends Union {
    constructor(Item) {
        super();
        this.tag = 0;
        this.fields = [Item];
    }
    cases() {
        return ["VscTestId"];
    }
}

export function VscTestId_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscTestId", [], VscTestId, () => [[["Item", string_type]]]);
}

export function VscTestIdModule_create(s) {
    return new VscTestId(s);
}

export function VscTestIdModule_value(_arg) {
    return _arg.fields[0];
}

export class VscTestInfo extends Record {
    constructor(Id, DisplayName, FullName, FilePath, Line) {
        super();
        this.Id = Id;
        this.DisplayName = DisplayName;
        this.FullName = FullName;
        this.FilePath = FilePath;
        this.Line = Line;
    }
}

export function VscTestInfo_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingTypes.VscTestInfo", [], VscTestInfo, () => [["Id", VscTestId_$reflection()], ["DisplayName", string_type], ["FullName", string_type], ["FilePath", option_type(string_type)], ["Line", option_type(int32_type)]]);
}

export class VscTestResult extends Record {
    constructor(Id, Outcome, DurationMs, Output) {
        super();
        this.Id = Id;
        this.Outcome = Outcome;
        this.DurationMs = DurationMs;
        this.Output = Output;
    }
}

export function VscTestResult_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingTypes.VscTestResult", [], VscTestResult, () => [["Id", VscTestId_$reflection()], ["Outcome", VscTestOutcome_$reflection()], ["DurationMs", option_type(float64_type)], ["Output", option_type(string_type)]]);
}

export class VscCoverageHealth extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["AllPassing", "SomeFailing"];
    }
}

export function VscCoverageHealth_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscCoverageHealth", [], VscCoverageHealth, () => [[], []]);
}

export class VscLineCoverage extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Covered", "NotCovered", "Pending"];
    }
}

export function VscLineCoverage_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscLineCoverage", [], VscLineCoverage, () => [[["testCount", int32_type], ["health", VscCoverageHealth_$reflection()]], [], []]);
}

export class VscFileCoverage extends Record {
    constructor(FilePath, LineCoverage, CoveredCount, TotalCount) {
        super();
        this.FilePath = FilePath;
        this.LineCoverage = LineCoverage;
        this.CoveredCount = (CoveredCount | 0);
        this.TotalCount = (TotalCount | 0);
    }
}

export function VscFileCoverage_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingTypes.VscFileCoverage", [], VscFileCoverage, () => [["FilePath", string_type], ["LineCoverage", class_type("Microsoft.FSharp.Collections.FSharpMap`2", [int32_type, VscLineCoverage_$reflection()])], ["CoveredCount", int32_type], ["TotalCount", int32_type]]);
}

export class VscRunPolicy extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["EveryKeystroke", "OnSave", "OnDemand", "Disabled"];
    }
}

export function VscRunPolicy_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscRunPolicy", [], VscRunPolicy, () => [[], [], [], []]);
}

export function VscRunPolicyModule_fromString(s) {
    const matchValue = s.toLowerCase();
    switch (matchValue) {
        case "every":
            return new VscRunPolicy(0, []);
        case "save":
            return new VscRunPolicy(1, []);
        case "demand":
            return new VscRunPolicy(2, []);
        case "disabled":
            return new VscRunPolicy(3, []);
        default:
            return undefined;
    }
}

export function VscRunPolicyModule_toString(p) {
    switch (p.tag) {
        case 1:
            return "save";
        case 2:
            return "demand";
        case 3:
            return "disabled";
        default:
            return "every";
    }
}

export class VscTestCategory extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Unit", "Integration", "Browser", "Benchmark", "Architecture", "Property"];
    }
}

export function VscTestCategory_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscTestCategory", [], VscTestCategory, () => [[], [], [], [], [], []]);
}

export function VscTestCategoryModule_fromString(s) {
    const matchValue = s.toLowerCase();
    switch (matchValue) {
        case "unit":
            return new VscTestCategory(0, []);
        case "integration":
            return new VscTestCategory(1, []);
        case "browser":
            return new VscTestCategory(2, []);
        case "benchmark":
            return new VscTestCategory(3, []);
        case "architecture":
            return new VscTestCategory(4, []);
        case "property":
            return new VscTestCategory(5, []);
        default:
            return undefined;
    }
}

export function VscTestCategoryModule_toString(c) {
    switch (c.tag) {
        case 1:
            return "integration";
        case 2:
            return "browser";
        case 3:
            return "benchmark";
        case 4:
            return "architecture";
        case 5:
            return "property";
        default:
            return "unit";
    }
}

export class VscLiveTestingEnabled extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["LiveTestingOn", "LiveTestingOff"];
    }
}

export function VscLiveTestingEnabled_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscLiveTestingEnabled", [], VscLiveTestingEnabled, () => [[], []]);
}

export class VscResultFreshness extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Fresh", "StaleCodeEdited", "StaleWrongGeneration"];
    }
}

export function VscResultFreshness_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscResultFreshness", [], VscResultFreshness, () => [[], [], []]);
}

export class VscLiveTestEvent extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["TestsDiscovered", "TestRunStarted", "TestResultBatch", "LiveTestingEnabled", "LiveTestingDisabled", "RunPolicyChanged", "PipelineTimingRecorded", "CoverageUpdated"];
    }
}

export function VscLiveTestEvent_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscLiveTestEvent", [], VscLiveTestEvent, () => [[["tests", array_type(VscTestInfo_$reflection())]], [["testIds", array_type(VscTestId_$reflection())]], [["results", array_type(VscTestResult_$reflection())], ["freshness", VscResultFreshness_$reflection()]], [], [], [["category", VscTestCategory_$reflection()], ["policy", VscRunPolicy_$reflection()]], [["treeSitterMs", float64_type], ["fcsMs", float64_type], ["executionMs", float64_type]], [["coverage", class_type("Microsoft.FSharp.Collections.FSharpMap`2", [string_type, VscFileCoverage_$reflection()])]]]);
}

export class VscTestSummary extends Record {
    constructor(Total, Passed, Failed, Running, Stale, Disabled) {
        super();
        this.Total = (Total | 0);
        this.Passed = (Passed | 0);
        this.Failed = (Failed | 0);
        this.Running = (Running | 0);
        this.Stale = (Stale | 0);
        this.Disabled = (Disabled | 0);
    }
}

export function VscTestSummary_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingTypes.VscTestSummary", [], VscTestSummary, () => [["Total", int32_type], ["Passed", int32_type], ["Failed", int32_type], ["Running", int32_type], ["Stale", int32_type], ["Disabled", int32_type]]);
}

export class VscStateChange extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["TestsAdded", "TestsStarted", "TestsCompleted", "ResultsStale", "EnabledChanged", "PolicyUpdated", "TimingUpdated", "CoverageRefreshed", "SummaryChanged"];
    }
}

export function VscStateChange_$reflection() {
    return union_type("SageFs.Vscode.LiveTestingTypes.VscStateChange", [], VscStateChange, () => [[["Item", array_type(VscTestInfo_$reflection())]], [["Item", array_type(VscTestId_$reflection())]], [["Item", array_type(VscTestResult_$reflection())]], [["Item", VscResultFreshness_$reflection()]], [["Item", VscLiveTestingEnabled_$reflection()]], [["Item1", VscTestCategory_$reflection()], ["Item2", VscRunPolicy_$reflection()]], [["treeSitterMs", float64_type], ["fcsMs", float64_type], ["executionMs", float64_type]], [["Item", class_type("Microsoft.FSharp.Collections.FSharpMap`2", [string_type, VscFileCoverage_$reflection()])]], [["Item", VscTestSummary_$reflection()]]]);
}

export class VscLiveTestState extends Record {
    constructor(Tests, Results, Coverage, RunningTests, Policies, Enabled, LastTiming, Freshness) {
        super();
        this.Tests = Tests;
        this.Results = Results;
        this.Coverage = Coverage;
        this.RunningTests = RunningTests;
        this.Policies = Policies;
        this.Enabled = Enabled;
        this.LastTiming = LastTiming;
        this.Freshness = Freshness;
    }
}

export function VscLiveTestState_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingTypes.VscLiveTestState", [], VscLiveTestState, () => [["Tests", class_type("Microsoft.FSharp.Collections.FSharpMap`2", [VscTestId_$reflection(), VscTestInfo_$reflection()])], ["Results", class_type("Microsoft.FSharp.Collections.FSharpMap`2", [VscTestId_$reflection(), VscTestResult_$reflection()])], ["Coverage", class_type("Microsoft.FSharp.Collections.FSharpMap`2", [string_type, VscFileCoverage_$reflection()])], ["RunningTests", class_type("Microsoft.FSharp.Collections.FSharpSet`1", [VscTestId_$reflection()])], ["Policies", class_type("Microsoft.FSharp.Collections.FSharpMap`2", [VscTestCategory_$reflection(), VscRunPolicy_$reflection()])], ["Enabled", VscLiveTestingEnabled_$reflection()], ["LastTiming", option_type(tuple_type(float64_type, float64_type, float64_type))], ["Freshness", VscResultFreshness_$reflection()]]);
}

export const VscLiveTestStateModule_empty = new VscLiveTestState(empty({
    Compare: compare,
}), empty({
    Compare: compare,
}), empty({
    Compare: comparePrimitives,
}), empty_1({
    Compare: compare,
}), empty({
    Compare: compare,
}), new VscLiveTestingEnabled(1, []), undefined, new VscResultFreshness(0, []));

/**
 * Pure fold: event → state → (new state * changes for UI)
 */
export function VscLiveTestStateModule_update(event, state) {
    switch (event.tag) {
        case 1: {
            const ids = event.fields[0];
            const running = ofArray(ids, {
                Compare: compare,
            });
            return [new VscLiveTestState(state.Tests, fold((m_1, id) => add(id, new VscTestResult(id, new VscTestOutcome(3, []), undefined, undefined), m_1), state.Results, ids), state.Coverage, running, state.Policies, state.Enabled, state.LastTiming, new VscResultFreshness(0, [])), singleton(new VscStateChange(1, [ids]))];
        }
        case 2: {
            const results_1 = event.fields[0];
            const freshness = event.fields[1];
            return [new VscLiveTestState(state.Tests, fold((m_2, r) => add(r.Id, r, m_2), state.Results, results_1), state.Coverage, difference(state.RunningTests, ofArray(map((r_1) => r_1.Id, results_1), {
                Compare: compare,
            })), state.Policies, state.Enabled, state.LastTiming, freshness), toList(delay(() => append(singleton_1(new VscStateChange(2, [results_1])), delay(() => (!equals(freshness, new VscResultFreshness(0, [])) ? singleton_1(new VscStateChange(3, [freshness])) : empty_2())))))];
        }
        case 3:
            return [new VscLiveTestState(state.Tests, state.Results, state.Coverage, state.RunningTests, state.Policies, new VscLiveTestingEnabled(0, []), state.LastTiming, state.Freshness), singleton(new VscStateChange(4, [new VscLiveTestingEnabled(0, [])]))];
        case 4:
            return [new VscLiveTestState(state.Tests, state.Results, state.Coverage, state.RunningTests, state.Policies, new VscLiveTestingEnabled(1, []), state.LastTiming, state.Freshness), singleton(new VscStateChange(4, [new VscLiveTestingEnabled(1, [])]))];
        case 5: {
            const pol = event.fields[1];
            const cat = event.fields[0];
            return [new VscLiveTestState(state.Tests, state.Results, state.Coverage, state.RunningTests, add(cat, pol, state.Policies), state.Enabled, state.LastTiming, state.Freshness), singleton(new VscStateChange(5, [cat, pol]))];
        }
        case 6: {
            const ts = event.fields[0];
            const fcs = event.fields[1];
            const exec = event.fields[2];
            return [new VscLiveTestState(state.Tests, state.Results, state.Coverage, state.RunningTests, state.Policies, state.Enabled, [ts, fcs, exec], state.Freshness), singleton(new VscStateChange(6, [ts, fcs, exec]))];
        }
        case 7: {
            const cov = event.fields[0];
            return [new VscLiveTestState(state.Tests, state.Results, cov, state.RunningTests, state.Policies, state.Enabled, state.LastTiming, state.Freshness), singleton(new VscStateChange(7, [cov]))];
        }
        default: {
            const tests = event.fields[0];
            return [new VscLiveTestState(fold((m, t) => add(t.Id, t, m), state.Tests, tests), state.Results, state.Coverage, state.RunningTests, state.Policies, state.Enabled, state.LastTiming, state.Freshness), singleton(new VscStateChange(0, [tests]))];
        }
    }
}

/**
 * Compute test summary from current state
 */
export function VscLiveTestStateModule_summary(state) {
    const total = FSharpMap__get_Count(state.Tests) | 0;
    let passed = 0;
    let failed = 0;
    let stale = 0;
    let disabled = 0;
    iterate((_arg, r) => {
        const matchValue = r.Outcome;
        switch (matchValue.tag) {
            case 0: {
                passed = ((passed + 1) | 0);
                break;
            }
            case 1:
            case 4: {
                failed = ((failed + 1) | 0);
                break;
            }
            case 5: {
                stale = ((stale + 1) | 0);
                break;
            }
            case 6: {
                disabled = ((disabled + 1) | 0);
                break;
            }
            default:
                undefined;
        }
    }, state.Results);
    const stale_1 = (!equals(state.Freshness, new VscResultFreshness(0, [])) ? count(filter((_arg_1, r_1) => {
        if (r_1.Outcome.tag === 3) {
            return false;
        }
        else {
            return true;
        }
    }, state.Results)) : stale) | 0;
    return new VscTestSummary(total, passed, failed, FSharpSet__get_Count(state.RunningTests), stale_1, disabled);
}

/**
 * Get tests for a specific file
 */
export function VscLiveTestStateModule_testsForFile(filePath, state) {
    return choose((tupledArg) => {
        const t = tupledArg[1];
        const matchValue = t.FilePath;
        let matchResult, fp_1;
        if (matchValue != null) {
            if (matchValue === filePath) {
                matchResult = 0;
                fp_1 = matchValue;
            }
            else {
                matchResult = 1;
            }
        }
        else {
            matchResult = 1;
        }
        switch (matchResult) {
            case 0:
                return t;
            default:
                return undefined;
        }
    }, toList_1(state.Tests));
}

/**
 * Look up a specific test result
 */
export function VscLiveTestStateModule_resultFor(testId, state) {
    return tryFind(testId, state.Results);
}

