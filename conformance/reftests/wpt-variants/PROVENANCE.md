# Generated variants of imported wpt reftests

These pages are written out from upstream reftests that drive themselves with a script, so they are
derived rather than copied: what was taken is the test's markup and its reference, and what was done to
it is the one thing the script did.

- Source: https://github.com/web-platform-tests/wpt (github.com/web-platform-tests/wpt)
- Revision: 2f57f45d21482f94b36123dfcec77ae7ab859bc1
- Local checkout: d:/_git0/Hutu/build/wpt/css

The engine runs no script. A pair whose picture a script decides is drawn as the document before it ran,
which for the pages below is either the default state the author meant to vary or an empty page, and the
comparison then answers nothing about the feature the test was written for. Each family is expanded the
way its script worked:

## css-text/text-spacing-trim

`support/variant-class.js` copies the address's `class` into the root element's class list and its `lang`
into the root's language, and `support/variant-feature.js` copies `style` and `feature` into the root's
style attribute - the combinations are carried in the address rather than written out as pages. Each
test's `<meta name="variant">` lines are therefore turned into one page per variant, with those
attributes written on an `<html>` element, and a copy of the reference carrying the same ones. A test
that declares no variant is copied as it stands. Written by `generate-text-spacing-trim.ps1` beside this
file, which reads the checkout named above.

## css-text/text-autospace

`text-autospace-dynamic-001` toggles a class that turns autospacing off and on, and
`text-autospace-dynamic-text-001` to `-004` append, insert, replace and delete characters with a forced
layout between edits. Both rest the same question - that a line re-shaped after the text around it
changed reaches the same picture as one shaped once - and both end in a state the page could have been
written in: the class list after the toggle, and the text after the last edit, which every one of the
four comes to. `text-autospace-edit-001` builds a second copy of each line one character at a time, so
the copy is written beside it. The references are the upstream ones, unmodified.

## css-text/hanging-punctuation

`hanging-punctuation-first-ascii-quote` is static; only its reference is scripted, and the script
measures a quotation mark and writes its width into `text-indent`. Ahem's every glyph is one em, so the
width is the 16px the initial size makes of one, and the reference is written with that indent. The test
is otherwise as upstream wrote it.

## What is not here

- `hanging-scrollable-001`, whose script scrolls the box: what it asks about is where a scrolled picture
  starts, which no static page can state.
- The `.xht` pairs under `hanging-punctuation` (`allow-end-001`, `force-end-001`, `last-001`): the corpus
  counts `.html` pages, and those are XHTML whose `<style>` blocks are wrapped in CDATA, which an HTML
  parser reads as part of the stylesheet.
- The `crashtests` folders, which carry no reference.
