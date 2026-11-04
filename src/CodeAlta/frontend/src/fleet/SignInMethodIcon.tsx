import { AppIcon } from "../AppIcon";
import { BrandIcon } from "../BrandIcon";

/** The sign-in methods whose brand has a mark of its own, by method id, and the brand each one is drawn from. */
const brandOf = { microsoft: "microsoft", google: "google", github: "github" } as const;

/** Whether a sign-in method is drawn with the mark of a brand: such a mark sits on a neutral background, never on a colored button. */
export function hasBrandMark(method: string): boolean {
  return Object.hasOwn(brandOf, method);
}

/**
 * The icon of a sign-in method, 16 px by default as the icon of a Blueprint button: the official mark of Microsoft (the four
 * squares) and Google (the colored "G") as the brands publish them, the GitHub mark in the color of the text, a key for a
 * passkey and a shield for the company's sign-in. Nothing for a method it does not know.
 */
export function SignInMethodIcon({ method, size = 16 }: { method: string; size?: number }) {
  if (Object.hasOwn(brandOf, method)) return <BrandIcon name={brandOf[method as keyof typeof brandOf]} size={size} className="sign-in-method-icon" />;
  if (method === "passkey") return <AppIcon name="secret" size={size} className="sign-in-method-icon" />;
  if (method === "company") return <AppIcon name="shield" size={size} className="sign-in-method-icon" />;
  return null;
}
