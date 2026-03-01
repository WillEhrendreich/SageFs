import { Record, Union } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { lambda_type, unit_type, float64_type, bool_type, list_type, int32_type, record_type, option_type, string_type, union_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { some, map, defaultArg } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { tryField } from "./JsHelpers.fs.js";
import { empty, ofArray } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { choose, map as map_1 } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";

export class VscDiffLineKind extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["Unchanged", "Added", "Removed", "Modified"];
    }
}

export function VscDiffLineKind_$reflection() {
    return union_type("SageFs.Vscode.FeatureTypes.VscDiffLineKind", [], VscDiffLineKind, () => [[], [], [], []]);
}

export class VscDiffLine extends Record {
    constructor(Kind, Text$, OldText) {
        super();
        this.Kind = Kind;
        this.Text = Text$;
        this.OldText = OldText;
    }
}

export function VscDiffLine_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscDiffLine", [], VscDiffLine, () => [["Kind", VscDiffLineKind_$reflection()], ["Text", string_type], ["OldText", option_type(string_type)]]);
}

export class VscDiffSummary extends Record {
    constructor(Added, Removed, Modified, Unchanged) {
        super();
        this.Added = (Added | 0);
        this.Removed = (Removed | 0);
        this.Modified = (Modified | 0);
        this.Unchanged = (Unchanged | 0);
    }
}

export function VscDiffSummary_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscDiffSummary", [], VscDiffSummary, () => [["Added", int32_type], ["Removed", int32_type], ["Modified", int32_type], ["Unchanged", int32_type]]);
}

export class VscEvalDiff extends Record {
    constructor(Lines, Summary, HasDiff) {
        super();
        this.Lines = Lines;
        this.Summary = Summary;
        this.HasDiff = HasDiff;
    }
}

export function VscEvalDiff_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscEvalDiff", [], VscEvalDiff, () => [["Lines", list_type(VscDiffLine_$reflection())], ["Summary", VscDiffSummary_$reflection()], ["HasDiff", bool_type]]);
}

export class VscCellNode extends Record {
    constructor(CellId, Source, Produces, Consumes, IsStale) {
        super();
        this.CellId = (CellId | 0);
        this.Source = Source;
        this.Produces = Produces;
        this.Consumes = Consumes;
        this.IsStale = IsStale;
    }
}

export function VscCellNode_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscCellNode", [], VscCellNode, () => [["CellId", int32_type], ["Source", string_type], ["Produces", list_type(string_type)], ["Consumes", list_type(string_type)], ["IsStale", bool_type]]);
}

export class VscCellEdge extends Record {
    constructor(From, To) {
        super();
        this.From = (From | 0);
        this.To = (To | 0);
    }
}

export function VscCellEdge_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscCellEdge", [], VscCellEdge, () => [["From", int32_type], ["To", int32_type]]);
}

export class VscCellGraph extends Record {
    constructor(Cells, Edges) {
        super();
        this.Cells = Cells;
        this.Edges = Edges;
    }
}

export function VscCellGraph_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscCellGraph", [], VscCellGraph, () => [["Cells", list_type(VscCellNode_$reflection())], ["Edges", list_type(VscCellEdge_$reflection())]]);
}

export class VscBindingInfo extends Record {
    constructor(Name, TypeSig, CellIndex, IsShadowed, ShadowedBy, ReferencedIn) {
        super();
        this.Name = Name;
        this.TypeSig = TypeSig;
        this.CellIndex = (CellIndex | 0);
        this.IsShadowed = IsShadowed;
        this.ShadowedBy = ShadowedBy;
        this.ReferencedIn = ReferencedIn;
    }
}

export function VscBindingInfo_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscBindingInfo", [], VscBindingInfo, () => [["Name", string_type], ["TypeSig", string_type], ["CellIndex", int32_type], ["IsShadowed", bool_type], ["ShadowedBy", list_type(int32_type)], ["ReferencedIn", list_type(int32_type)]]);
}

export class VscBindingScopeSnapshot extends Record {
    constructor(Bindings, ActiveCount, ShadowedCount) {
        super();
        this.Bindings = Bindings;
        this.ActiveCount = (ActiveCount | 0);
        this.ShadowedCount = (ShadowedCount | 0);
    }
}

export function VscBindingScopeSnapshot_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscBindingScopeSnapshot", [], VscBindingScopeSnapshot, () => [["Bindings", list_type(VscBindingInfo_$reflection())], ["ActiveCount", int32_type], ["ShadowedCount", int32_type]]);
}

export class VscTimelineEntry extends Record {
    constructor(CellId, DurationMs, Status, Timestamp) {
        super();
        this.CellId = (CellId | 0);
        this.DurationMs = DurationMs;
        this.Status = Status;
        this.Timestamp = Timestamp;
    }
}

export function VscTimelineEntry_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscTimelineEntry", [], VscTimelineEntry, () => [["CellId", int32_type], ["DurationMs", float64_type], ["Status", string_type], ["Timestamp", float64_type]]);
}

export class VscTimelineStats extends Record {
    constructor(Count, P50Ms, P95Ms, P99Ms, MeanMs, Sparkline) {
        super();
        this.Count = (Count | 0);
        this.P50Ms = P50Ms;
        this.P95Ms = P95Ms;
        this.P99Ms = P99Ms;
        this.MeanMs = MeanMs;
        this.Sparkline = Sparkline;
    }
}

export function VscTimelineStats_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscTimelineStats", [], VscTimelineStats, () => [["Count", int32_type], ["P50Ms", option_type(float64_type)], ["P95Ms", option_type(float64_type)], ["P99Ms", option_type(float64_type)], ["MeanMs", option_type(float64_type)], ["Sparkline", string_type]]);
}

export class VscNotebookCell extends Record {
    constructor(Index, Label, Code, Output, Deps, Bindings) {
        super();
        this.Index = (Index | 0);
        this.Label = Label;
        this.Code = Code;
        this.Output = Output;
        this.Deps = Deps;
        this.Bindings = Bindings;
    }
}

export function VscNotebookCell_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.VscNotebookCell", [], VscNotebookCell, () => [["Index", int32_type], ["Label", option_type(string_type)], ["Code", string_type], ["Output", option_type(string_type)], ["Deps", list_type(int32_type)], ["Bindings", list_type(string_type)]]);
}

export class VscFeatureEvent extends Union {
    constructor(tag, fields) {
        super();
        this.tag = tag;
        this.fields = fields;
    }
    cases() {
        return ["EvalDiffReceived", "CellGraphReceived", "BindingScopeReceived", "TimelineReceived"];
    }
}

export function VscFeatureEvent_$reflection() {
    return union_type("SageFs.Vscode.FeatureTypes.VscFeatureEvent", [], VscFeatureEvent, () => [[["Item", VscEvalDiff_$reflection()]], [["Item", VscCellGraph_$reflection()]], [["Item", VscBindingScopeSnapshot_$reflection()]], [["Item", VscTimelineStats_$reflection()]]]);
}

export class FeatureCallbacks extends Record {
    constructor(OnEvalDiff, OnCellGraph, OnBindingScope, OnTimeline) {
        super();
        this.OnEvalDiff = OnEvalDiff;
        this.OnCellGraph = OnCellGraph;
        this.OnBindingScope = OnBindingScope;
        this.OnTimeline = OnTimeline;
    }
}

export function FeatureCallbacks_$reflection() {
    return record_type("SageFs.Vscode.FeatureTypes.FeatureCallbacks", [], FeatureCallbacks, () => [["OnEvalDiff", lambda_type(VscEvalDiff_$reflection(), unit_type)], ["OnCellGraph", lambda_type(VscCellGraph_$reflection(), unit_type)], ["OnBindingScope", lambda_type(VscBindingScopeSnapshot_$reflection(), unit_type)], ["OnTimeline", lambda_type(VscTimelineStats_$reflection(), unit_type)]]);
}

export function parseDiffLine(data) {
    const kindStr = defaultArg(tryField("kind", data), "unchanged");
    return new VscDiffLine((kindStr === "added") ? (new VscDiffLineKind(1, [])) : ((kindStr === "removed") ? (new VscDiffLineKind(2, [])) : ((kindStr === "modified") ? (new VscDiffLineKind(3, [])) : (new VscDiffLineKind(0, [])))), defaultArg(tryField("text", data), ""), tryField("oldText", data));
}

export function parseDiffSummary(data) {
    return new VscDiffSummary(defaultArg(tryField("added", data), 0), defaultArg(tryField("removed", data), 0), defaultArg(tryField("modified", data), 0), defaultArg(tryField("unchanged", data), 0));
}

export function parseEvalDiff(data) {
    let value_1;
    return new VscEvalDiff(defaultArg(map((arg) => ofArray(map_1(parseDiffLine, arg)), tryField("lines", data)), empty()), (value_1 = (new VscDiffSummary(0, 0, 0, 0)), defaultArg(map(parseDiffSummary, tryField("summary", data)), value_1)), defaultArg(tryField("hasDiff", data), false));
}

export function parseCellNode(data) {
    return new VscCellNode(defaultArg(tryField("cellId", data), 0), defaultArg(tryField("source", data), ""), defaultArg(map((arg_1) => ofArray(choose((arg) => {
        let x_1;
        return map((value_2) => value_2, (x_1 = arg, (x_1 == null) ? undefined : some(x_1)));
    }, arg_1)), tryField("produces", data)), empty()), defaultArg(map((arg_3) => ofArray(choose((arg_2) => {
        let x_3;
        return map((value_4) => value_4, (x_3 = arg_2, (x_3 == null) ? undefined : some(x_3)));
    }, arg_3)), tryField("consumes", data)), empty()), defaultArg(tryField("isStale", data), false));
}

export function parseCellGraph(data) {
    return new VscCellGraph(defaultArg(map((arg) => ofArray(map_1(parseCellNode, arg)), tryField("cells", data)), empty()), defaultArg(map((arg_1) => ofArray(map_1((e) => (new VscCellEdge(defaultArg(tryField("from", e), 0), defaultArg(tryField("to", e), 0))), arg_1)), tryField("edges", data)), empty()));
}

export function parseBindingInfo(data) {
    return new VscBindingInfo(defaultArg(tryField("name", data), ""), defaultArg(tryField("typeSig", data), ""), defaultArg(tryField("cellIndex", data), 0), defaultArg(tryField("isShadowed", data), false), defaultArg(map((arg_1) => ofArray(choose((arg) => {
        let x_1;
        return map((value_4) => value_4, (x_1 = arg, (x_1 == null) ? undefined : some(x_1)));
    }, arg_1, Int32Array)), tryField("shadowedBy", data)), empty()), defaultArg(map((arg_3) => ofArray(choose((arg_2) => {
        let x_3;
        return map((value_6) => value_6, (x_3 = arg_2, (x_3 == null) ? undefined : some(x_3)));
    }, arg_3, Int32Array)), tryField("referencedIn", data)), empty()));
}

export function parseBindingScopeSnapshot(data) {
    return new VscBindingScopeSnapshot(defaultArg(map((arg) => ofArray(map_1(parseBindingInfo, arg)), tryField("bindings", data)), empty()), defaultArg(tryField("activeCount", data), 0), defaultArg(tryField("shadowedCount", data), 0));
}

export function parseTimelineStats(data) {
    return new VscTimelineStats(defaultArg(tryField("count", data), 0), tryField("p50Ms", data), tryField("p95Ms", data), tryField("p99Ms", data), tryField("meanMs", data), defaultArg(tryField("sparkline", data), ""));
}

export function parseNotebookCell(data) {
    return new VscNotebookCell(defaultArg(tryField("index", data), 0), tryField("label", data), defaultArg(tryField("code", data), ""), tryField("output", data), defaultArg(map((arg_1) => ofArray(choose((arg) => {
        let x_1;
        return map((value_2) => value_2, (x_1 = arg, (x_1 == null) ? undefined : some(x_1)));
    }, arg_1, Int32Array)), tryField("deps", data)), empty()), defaultArg(map((arg_3) => ofArray(choose((arg_2) => {
        let x_3;
        return map((value_4) => value_4, (x_3 = arg_2, (x_3 == null) ? undefined : some(x_3)));
    }, arg_3)), tryField("bindings", data)), empty()));
}

export function processFeatureEvent(eventType, data, callbacks) {
    switch (eventType) {
        case "eval_diff": {
            callbacks.OnEvalDiff(parseEvalDiff(data));
            break;
        }
        case "cell_dependencies": {
            callbacks.OnCellGraph(parseCellGraph(data));
            break;
        }
        case "binding_scope_map": {
            callbacks.OnBindingScope(parseBindingScopeSnapshot(data));
            break;
        }
        case "eval_timeline": {
            callbacks.OnTimeline(parseTimelineStats(data));
            break;
        }
        default:
            undefined;
    }
}

export function formatSparklineStatus(stats) {
    let clo;
    if (stats.Count === 0) {
        return "";
    }
    else {
        const p50 = defaultArg(map((clo = toText(printf("p50=%.0fms")), clo), stats.P50Ms), "");
        return toText(printf("⚡ %s [%d] %s"))(stats.Sparkline)(stats.Count)(p50);
    }
}

