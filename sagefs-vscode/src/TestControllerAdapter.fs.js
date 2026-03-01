import { FSharpRef, Record } from "./fable_modules/fable-library-js.4.29.0/Types.js";
import { record_type, lambda_type, unit_type, list_type, class_type } from "./fable_modules/fable-library-js.4.29.0/Reflection.js";
import { VscTestIdModule_value, VscStateChange_$reflection } from "./LiveTestingTypes.fs.js";
import { newTestMessage, newRange, uriFile, Tests_createTestController } from "./Vscode.fs.js";
import { iterate } from "./fable_modules/fable-library-js.4.29.0/Seq.js";
import { some, defaultArg, map, toArray } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { disposeSafe, getEnumerator, defaultOf } from "./fable_modules/fable-library-js.4.29.0/Util.js";
import { PromiseBuilder__Delay_62FBFDE1, PromiseBuilder__Run_212F1D4B } from "./fable_modules/Fable.Promise.3.2.0/Promise.fs.js";
import { item as item_4 } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { promise } from "./fable_modules/Fable.Promise.3.2.0/PromiseImpl.fs.js";
import { runTests } from "./SageFsClient.fs.js";
import { tryGetValue } from "./fable_modules/fable-library-js.4.29.0/MapUtil.js";

export class TestAdapter extends Record {
    constructor(Controller, Refresh, Dispose) {
        super();
        this.Controller = Controller;
        this.Refresh = Refresh;
        this.Dispose = Dispose;
    }
}

export function TestAdapter_$reflection() {
    return record_type("SageFs.Vscode.TestControllerAdapter.TestAdapter", [], TestAdapter, () => [["Controller", class_type("Vscode.TestController")], ["Refresh", lambda_type(list_type(VscStateChange_$reflection()), unit_type)], ["Dispose", lambda_type(unit_type, unit_type)]]);
}

export function create(getClient) {
    const controller = Tests_createTestController("sagefs", "SageFs Live Tests");
    const testItemMap = new Map([]);
    let activeRun = undefined;
    let endRunTimer = undefined;
    const endActiveRun = () => {
        iterate((id) => {
            clearTimeout(id);
        }, toArray(endRunTimer));
        endRunTimer = undefined;
        iterate((r) => {
            r.end();
        }, toArray(activeRun));
        activeRun = undefined;
    };
    const getOrCreateRun = () => {
        if (activeRun == null) {
            const request = {
                include: defaultOf(),
                exclude: defaultOf(),
            };
            const run_1 = controller.createTestRun(request);
            activeRun = run_1;
            return run_1;
        }
        else {
            return activeRun;
        }
    };
    const _runProfile = controller.createRunProfile("Run Tests", (1), ((req, token) => PromiseBuilder__Run_212F1D4B(promise, PromiseBuilder__Delay_62FBFDE1(promise, () => {
        let items;
        const matchValue_4 = getClient();
        if (matchValue_4 != null) {
            const c = matchValue_4;
            let pattern;
            const matchValue_5 = req.include;
            let matchResult, items_1;
            if (matchValue_5 != null) {
                if ((items = matchValue_5, items.length > 0)) {
                    matchResult = 0;
                    items_1 = matchValue_5;
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
                    pattern = item_4(0, items_1).id;
                    break;
                }
                default:
                    pattern = "";
            }
            return runTests(pattern, c).then((_arg) => {
                return Promise.resolve();
            });
        }
        else {
            return Promise.resolve();
        }
    }))), true);
    return new TestAdapter(controller, (changes) => {
        let info, id_2, matchValue, outArg, item, uri, item_1, u, matchValue_1, line;
        const enumerator = getEnumerator(changes);
        try {
            while (enumerator["System.Collections.IEnumerator.MoveNext"]()) {
                const change = enumerator["System.Collections.Generic.IEnumerator`1.get_Current"]();
                switch (change.tag) {
                    case 0: {
                        const tests = change.fields[0];
                        for (let idx_1 = 0; idx_1 <= (tests.length - 1); idx_1++) {
                            (info = item_4(idx_1, tests), (id_2 = VscTestIdModule_value(info.Id), (matchValue = ((outArg = defaultOf(), [tryGetValue(testItemMap, id_2, new FSharpRef(() => outArg, (v) => {
                                outArg = v;
                            })), outArg])), matchValue[0] ? ((item = matchValue[1], (item.label = info.DisplayName, item))) : ((uri = map(uriFile, info.FilePath), (item_1 = ((uri == null) ? (controller.createTestItem(id_2, info.DisplayName, undefined)) : ((u = uri, controller.createTestItem(id_2, info.DisplayName, u)))), ((matchValue_1 = info.Line, (matchValue_1 == null) ? undefined : ((line = (matchValue_1 | 0), item_1.range = newRange(line - 1, 0, line - 1, 0)))), (controller.items.add(item_1), (testItemMap.set(id_2, item_1), item_1)))))))));
                        }
                        break;
                    }
                    case 2: {
                        const results = change.fields[0];
                        const run_2 = getOrCreateRun();
                        for (let idx = 0; idx <= (results.length - 1); idx++) {
                            const result = item_4(idx, results);
                            const id_3 = VscTestIdModule_value(result.Id);
                            let matchValue_2;
                            let outArg_1 = defaultOf();
                            matchValue_2 = [tryGetValue(testItemMap, id_3, new FSharpRef(() => outArg_1, (v_1) => {
                                outArg_1 = v_1;
                            })), outArg_1];
                            if (matchValue_2[0]) {
                                const item_2 = matchValue_2[1];
                                const durationMs = defaultArg(result.DurationMs, 0);
                                const matchValue_3 = result.Outcome;
                                switch (matchValue_3.tag) {
                                    case 1: {
                                        const message = newTestMessage(matchValue_3.fields[0]);
                                        run_2.failed(item_2, message, durationMs);
                                        break;
                                    }
                                    case 2: {
                                        run_2.skipped(item_2);
                                        break;
                                    }
                                    case 3: {
                                        run_2.started(item_2);
                                        break;
                                    }
                                    case 4: {
                                        const message_1 = newTestMessage(matchValue_3.fields[0]);
                                        run_2.failed(item_2, message_1, durationMs);
                                        break;
                                    }
                                    case 5: {
                                        run_2.skipped(item_2);
                                        break;
                                    }
                                    case 6: {
                                        run_2.skipped(item_2);
                                        break;
                                    }
                                    default:
                                        run_2.passed(item_2, durationMs);
                                }
                            }
                        }
                        iterate((id_1) => {
                            clearTimeout(id_1);
                        }, toArray(endRunTimer));
                        endRunTimer = some(setTimeout((() => {
                            endActiveRun();
                        }), 500));
                        break;
                    }
                    case 1: {
                        const ids = change.fields[0];
                        endActiveRun();
                        const run_3 = getOrCreateRun();
                        for (let idx_2 = 0; idx_2 <= (ids.length - 1); idx_2++) {
                            const idStr = VscTestIdModule_value(item_4(idx_2, ids));
                            let matchValue_6;
                            let outArg_2 = defaultOf();
                            matchValue_6 = [tryGetValue(testItemMap, idStr, new FSharpRef(() => outArg_2, (v_2) => {
                                outArg_2 = v_2;
                            })), outArg_2];
                            if (matchValue_6[0]) {
                                run_3.started(matchValue_6[1]);
                            }
                        }
                        break;
                    }
                    default:
                        undefined;
                }
            }
        }
        finally {
            disposeSafe(enumerator);
        }
    }, () => {
        endActiveRun();
        controller.dispose();
    });
}

