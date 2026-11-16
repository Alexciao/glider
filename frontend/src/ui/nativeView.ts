import { create } from "zustand";

/**
 * Whether a native view (the Store's WebView2) is on screen over the page. It is a separate window above the HTML, so a popup
 * that reaches into its rectangle is drawn underneath it: tooltips read this to stay out of its way.
 */
const useNativeView = create<{ onScreen: boolean }>(() => ({ onScreen: false }));

export const useNativeViewOnScreen = (): boolean => useNativeView((s) => s.onScreen);
export const setNativeViewOnScreen = (onScreen: boolean): void => useNativeView.setState({ onScreen });
