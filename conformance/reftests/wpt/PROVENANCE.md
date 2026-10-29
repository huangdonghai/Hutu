# Imported wpt reftests

These reftest pairs were taken, unmodified apart from their reference link, from an upstream
test suite. They are under the upstream licence, not this project's.

- Source: https://github.com/web-platform-tests/wpt (github.com/web-platform-tests/wpt)
- Revision: 2f57f45d21482f94b36123dfcec77ae7ab859bc1
- Local checkout: d:/_git0/Hutu/build/wpt/css

A test that uses the feature and a reference that reaches the same picture by other means are
copied beside each other; the reference is renamed after the test so the pair is self-contained.

A later pass took back out the pairs that drive themselves with script - a page carrying a
`<script>`, an event handler written on the element, or a `javascript:` url - 12 pairs here, 136
in the gecko tree. This engine runs no script, and a pair whose picture a script decides is drawn
here as the document before it ran: both sides show the same untouched page, so the comparison
answers nothing about the feature the test was written for. What is left is static layout, and
every pair in it is a claim the engine is given the way to reach.
