# Coles parser fixture

`coles-1849307.html` is a reduced snapshot of the JSON-LD and `__NEXT_DATA__.props.pageProps.product` fields read from the public page on 2026-09-19:

https://www.coles.com.au/product/coca-cola-classic-soft-drink-multipack-cans-375ml-10-pack-1849307

Only product and price structures required for extraction remain. Account, session, checkout, and unrelated page state were removed. Description prose was replaced with short factual pack expressions reproducing the observed contradiction (10 versus 24 cans). One variation was reduced to its ID, name, size, and price. The fixture's single-pack price is 23 AUD; its multibuy wording is separate. These are historical test values, not current offers.

The observed structures are a top-level schema.org Product with numeric sku, gtin, brand.name, an offers array, and an embedded primary product with id/name/brand/size/gtin/pricing. The latter contains pricing.now/was/unit, promotion fields, images[].full.path, and variations.bySizes. The parser never scans variations for the source offer. Other test inputs are explicitly synthetic variations of this documented structure, not claimed retailer snapshots.
