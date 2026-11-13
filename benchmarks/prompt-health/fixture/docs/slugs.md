# Slugs

`slugify` of `src/text.js` makes the slug of a product name, used in the URLs of the shop: `Crème brûlée 250g` gives `creme-brulee-250g`.

Accented letters are replaced using the table `ACCENTS` of `src/text.js`. A letter that is missing from the table is dropped from the slug.
