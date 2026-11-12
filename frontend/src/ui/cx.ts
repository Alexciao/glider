export type ClassValue = string | false | null | undefined;

/** Join class names, dropping falsy values. */
export function cx(...values: ClassValue[]): string {
  let out = "";
  for (const v of values) if (v) out += out ? ` ${v}` : v;
  return out;
}
