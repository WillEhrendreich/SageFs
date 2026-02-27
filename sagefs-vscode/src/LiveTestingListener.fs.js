import { VscLiveTestStateModule_summary, VscLiveTestStateModule_update, VscLiveTestStateModule_empty, VscLiveTestState_$reflection, VscTestSummary_$reflection, VscStateChange_$reflection, VscLiveTestEvent, VscResultFreshness, VscTestInfo, VscTestResult, VscTestOutcome, VscTestIdModule_create, VscTestSummary } from "./LiveTestingTypes.fs.js";
import { Record, toString } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { map, item } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { parse } from "./fable_modules/fable-library-js.4.29.0/Double.js";
import { isEmpty, append, ofArray, empty } from "./fable_modules/fable-library-js.4.29.0/List.js";
import { record_type, lambda_type, unit_type, list_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { disposeSafe, getEnumerator } from "./fable_modules/fable-library-js.4.29.0/Util.js";

/**
 * Map server TestSummary JSON to VscTestSummary
 */
export function parseSummary(data) {
    return new VscTestSummary(data.Total, data.Passed, data.Failed, data.Running, data.Stale, data.Disabled);
}

/**
 * Map a server TestStatusEntry to VscTestResult
 */
export function parseTestResult(entry) {
    let fields, failObj, failFields, dur, parts, fields_3, dur_1, parts_1;
    const testIdObj = entry.TestId;
    const id = VscTestIdModule_create((testIdObj == null) ? "" : ((fields = testIdObj.Fields, (fields == null) ? toString(testIdObj) : item(0, fields))));
    const status = entry.Status;
    const statusCase = (status == null) ? "Detected" : status.Case;
    return new VscTestResult(id, (statusCase === "Passed") ? (new VscTestOutcome(0, [])) : ((statusCase === "Failed") ? ((failObj = item(0, status.Fields), new VscTestOutcome(1, [(failObj == null) ? "test failed" : ((failFields = failObj.Fields, (failFields == null) ? toString(failObj) : item(0, failFields)))]))) : ((statusCase === "Skipped") ? (new VscTestOutcome(2, [item(0, status.Fields)])) : ((statusCase === "Running") ? (new VscTestOutcome(3, [])) : ((statusCase === "Stale") ? (new VscTestOutcome(5, [])) : ((statusCase === "PolicyDisabled") ? (new VscTestOutcome(6, [])) : (new VscTestOutcome(2, ["unknown status"]))))))), (statusCase === "Passed") ? ((dur = item(0, status.Fields), (parts = dur.split(":"), (parts.length === 3) ? ((((parse(item(0, parts)) * 3600) + (parse(item(1, parts)) * 60)) + parse(item(2, parts))) * 1000) : undefined))) : ((statusCase === "Failed") ? ((fields_3 = status.Fields, (fields_3.length >= 2) ? ((dur_1 = item(1, fields_3), (parts_1 = dur_1.split(":"), (parts_1.length === 3) ? ((((parse(item(0, parts_1)) * 3600) + (parse(item(1, parts_1)) * 60)) + parse(item(2, parts_1))) * 1000) : undefined))) : undefined)) : undefined), undefined);
}

/**
 * Map a server TestStatusEntry to VscTestInfo
 */
export function parseTestInfo(entry) {
    const testIdObj = entry.TestId;
    let testIdStr;
    if (testIdObj == null) {
        testIdStr = "";
    }
    else {
        const fields = testIdObj.Fields;
        testIdStr = ((fields == null) ? toString(testIdObj) : item(0, fields));
    }
    const origin = entry.Origin;
    let patternInput;
    if (origin == null) {
        patternInput = [undefined, undefined];
    }
    else if (origin.Case === "SourceMapped") {
        const fields_1 = origin.Fields;
        patternInput = [item(0, fields_1), item(1, fields_1)];
    }
    else {
        patternInput = [undefined, undefined];
    }
    return new VscTestInfo(VscTestIdModule_create(testIdStr), entry.DisplayName, entry.FullName, patternInput[0], patternInput[1]);
}

/**
 * Parse Freshness DU from server JSON (Case/Fields or plain string)
 */
export function parseFreshness(data) {
    const freshnessObj = data.Freshness;
    if (freshnessObj == null) {
        return new VscResultFreshness(0, []);
    }
    else {
        let caseStr;
        const c = freshnessObj.Case;
        caseStr = ((c == null) ? toString(freshnessObj) : c);
        switch (caseStr) {
            case "StaleCodeEdited":
                return new VscResultFreshness(1, []);
            case "StaleWrongGeneration":
                return new VscResultFreshness(2, []);
            default:
                return new VscResultFreshness(0, []);
        }
    }
}

/**
 * Parse test_results_batch → VscLiveTestEvent pair (discovery + results)
 */
export function parseResultsBatch(data) {
    const entries = data.Entries;
    if (entries == null) {
        return empty();
    }
    else {
        const freshness = parseFreshness(data);
        const entryArray = entries;
        return ofArray([new VscLiveTestEvent(0, [map(parseTestInfo, entryArray)]), new VscLiveTestEvent(2, [map(parseTestResult, entryArray), freshness])]);
    }
}

export class LiveTestingCallbacks extends Record {
    constructor(OnStateChange, OnSummaryUpdate, OnStatusRefresh) {
        super();
        this.OnStateChange = OnStateChange;
        this.OnSummaryUpdate = OnSummaryUpdate;
        this.OnStatusRefresh = OnStatusRefresh;
    }
}

export function LiveTestingCallbacks_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingListener.LiveTestingCallbacks", [], LiveTestingCallbacks, () => [["OnStateChange", lambda_type(list_type(VscStateChange_$reflection()), unit_type)], ["OnSummaryUpdate", lambda_type(VscTestSummary_$reflection(), unit_type)], ["OnStatusRefresh", lambda_type(unit_type, unit_type)]]);
}

export class LiveTestingListener extends Record {
    constructor(State, Summary, Dispose) {
        super();
        this.State = State;
        this.Summary = Summary;
        this.Dispose = Dispose;
    }
}

export function LiveTestingListener_$reflection() {
    return record_type("SageFs.Vscode.LiveTestingListener.LiveTestingListener", [], LiveTestingListener, () => [["State", lambda_type(unit_type, VscLiveTestState_$reflection())], ["Summary", lambda_type(unit_type, VscTestSummary_$reflection())], ["Dispose", lambda_type(unit_type, unit_type)]]);
}

export function start(port, callbacks) {
    let state = VscLiveTestStateModule_empty;
    const url = toText(printf("http://localhost:%d/events"))(port);
    const disposable = (() => {
  const http = require('http');
  let req;
  let buffer = '';
  let currentEvent = 'message';
  let retryDelay = 1000;
  const maxDelay = 30000;
  const startListening = () => {
    req = http.get(url, { timeout: 0 }, (res) => {
      retryDelay = 1000;
      res.on('data', (chunk) => {
        buffer += chunk.toString();
        let lines = buffer.split('\n');
        buffer = lines.pop() || '';
        for (const line of lines) {
          if (line.startsWith('event: ')) {
            currentEvent = line.slice(7).trim();
          } else if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              ((eventType, data) => {
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
            default:
                undefined;
        }
    })(currentEvent, data);
            } catch (_) {}
            currentEvent = 'message';
          } else if (line.trim() === '') {
            currentEvent = 'message';
          }
        }
      });
      res.on('end', () => {
        retryDelay = Math.min(retryDelay * 2, maxDelay);
        setTimeout(startListening, retryDelay);
      });
      res.on('error', () => {
        retryDelay = Math.min(retryDelay * 2, maxDelay);
        setTimeout(startListening, retryDelay);
      });
    });
    req.on('error', () => {
      retryDelay = Math.min(retryDelay * 2, maxDelay);
      setTimeout(startListening, retryDelay);
    });
  };
  startListening();
  return { dispose: () => { if (req) req.destroy(); } };
})();
    return new LiveTestingListener(() => state, () => VscLiveTestStateModule_summary(state), () => {
        disposable.dispose();
    });
}

