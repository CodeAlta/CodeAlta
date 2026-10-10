// The module a plugin gets for `react`: the instance the application runs. A second copy of React in the page would
// break hooks and contexts, so the build emits this file as its own entry (`lib/react.js`) that shares its code with the
// application, and the import map of `index.html` points the bare name at it. Names are listed one by one because React
// is CommonJS; `lent.test.ts` compares them with what the package exports.
import * as React from "react";
export {
  Activity, Children, Component, Fragment, Profiler, PureComponent, StrictMode, Suspense, act, cache, cacheSignal, captureOwnerStack, cloneElement,
  createContext, createElement, createRef, forwardRef, isValidElement, lazy, memo, startTransition, use, useActionState,
  useCallback, useContext, useDebugValue, useDeferredValue, useEffect, useEffectEvent, useId, useImperativeHandle, useInsertionEffect, useLayoutEffect,
  useMemo, useOptimistic, useReducer, useRef, useState, useSyncExternalStore, useTransition, version,
} from "react";
export default React;
