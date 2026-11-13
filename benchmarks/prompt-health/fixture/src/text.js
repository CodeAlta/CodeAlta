// Accented letters and the ASCII letter each one becomes in a slug.
const ACCENTS = { "à": "a", "â": "a", "ä": "a", "ç": "c", "é": "e", "è": "e", "ê": "e", "ë": "e", "î": "i", "ï": "i", "ô": "o", "ö": "o", "ù": "u", "û": "u", "ü": "u" };

function replaceAccents(text) {
  let result = "";
  for (const letter of text) result += ACCENTS[letter] ?? letter;
  return result;
}

/** Makes the slug of a product name: "Crème brûlée 250g" gives "creme-brulee-250g". */
export function slugify(name) {
  return replaceAccents(name.toLowerCase()).replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
}
