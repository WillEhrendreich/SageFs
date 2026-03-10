import { orElse, bind, value, some, map } from "./fable_modules/fable-library-js.4.29.0/Option.js";
import { item, choose } from "./fable_modules/fable-library-js.4.29.0/Array.js";
import { printf, toText } from "./fable_modules/fable-library-js.4.29.0/String.js";
import { toString } from "./fable_modules/fable-library-js.4.29.0/Types.js";

export const tryCastString = (x_1) => {
    const x_2 = x_1;
    if ((x_2 == null)) {
        return undefined;
    }
    else {
        const x = x_2;
        return ((typeof x) === "string") ? x : undefined;
    }
};

export const tryCastInt = (x_1) => {
    const x_2 = x_1;
    if ((x_2 == null)) {
        return undefined;
    }
    else {
        const x = x_2;
        return (Number.isInteger(x)) ? x : undefined;
    }
};

export const tryCastFloat = (x_1) => {
    const x_2 = x_1;
    if ((x_2 == null)) {
        return undefined;
    }
    else {
        const x = x_2;
        return ((typeof x) === "number") ? x : undefined;
    }
};

export const tryCastBool = (x_1) => {
    const x_2 = x_1;
    if ((x_2 == null)) {
        return undefined;
    }
    else {
        const x = x_2;
        return ((typeof x) === "boolean") ? x : undefined;
    }
};

export const tryCastArray = (x_1) => {
    const x_2 = x_1;
    if ((x_2 == null)) {
        return undefined;
    }
    else {
        const x = x_2;
        return (Array.isArray(x)) ? x : undefined;
    }
};

export function tryCastStringArray(x) {
    return map((array) => choose(tryCastString, array), tryCastArray(x));
}

export function tryCastIntArray(x) {
    return map((array) => choose(tryCastInt, array, Int32Array), tryCastArray(x));
}

function rawField(name, obj) {
    if ((obj == null)) {
        return undefined;
    }
    else {
        const v = obj[name];
        if ((v == null)) {
            return undefined;
        }
        else {
            return some(v);
        }
    }
}

function fieldWithCast(typeName, cast, name, obj) {
    let arg_2;
    const matchValue = rawField(name, obj);
    if (matchValue != null) {
        const v = value(matchValue);
        const matchValue_1 = cast(v);
        if (matchValue_1 == null) {
            console.warn('[SageFs]', ((arg_2 = (typeof v), toText(printf("Field \'%s\': expected %s, got %s"))(name)(typeName)(arg_2))));
            return undefined;
        }
        else {
            return some(value(matchValue_1));
        }
    }
    else {
        return undefined;
    }
}

export function fieldString(name, obj) {
    return fieldWithCast("string", tryCastString, name, obj);
}

export function fieldInt(name, obj) {
    return fieldWithCast("int", tryCastInt, name, obj);
}

export function fieldFloat(name, obj) {
    return fieldWithCast("float", tryCastFloat, name, obj);
}

export function fieldBool(name, obj) {
    return fieldWithCast("boolean", tryCastBool, name, obj);
}

export function fieldArray(name, obj) {
    return fieldWithCast("array", tryCastArray, name, obj);
}

export const fieldObj = (name) => ((obj) => rawField(name, obj));

export function fieldStringArray(name, obj) {
    return bind(tryCastStringArray, rawField(name, obj));
}

export function fieldIntArray(name, obj) {
    return bind(tryCastIntArray, rawField(name, obj));
}

export function duCase(du) {
    let x_1;
    return orElse(fieldString("Case", du), (x_1 = du, ((x_1 == null)) ? undefined : toString(x_1)));
}

export function duFieldsArray(du) {
    return bind(tryCastArray, rawField("Fields", du));
}

export function duFirstFieldString(du) {
    return bind((arr) => {
        if (arr.length === 0) {
            return undefined;
        }
        else {
            return tryCastString(item(0, arr));
        }
    }, duFieldsArray(du));
}

export function duFirstFieldInt(du) {
    return bind((arr) => {
        if (arr.length === 0) {
            return undefined;
        }
        else {
            return tryCastInt(item(0, arr));
        }
    }, duFieldsArray(du));
}

export function tryHandleEvent(eventType, fn) {
    try {
        fn();
    }
    catch (ex) {
        console.error('[SageFs]', toText(printf("SSE handler error for \'%s\'"))(eventType), ex);
    }
}

