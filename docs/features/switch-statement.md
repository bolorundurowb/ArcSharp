# Constant-label switch statements

`switch` evaluates its input once and chooses a section by its constant labels
or `default`. A section can have multiple labels. Strings use value equality,
and a null input can match a null label. A `break` exits the switch, while a
`continue` inside a switch in a loop advances the enclosing loop.

The emitter compares the input with the labels and uses the selected section
index for its LLVM branch. It releases all dispatch temporaries before
entering a section. The case graph participates in definite-assignment analysis
so a variable assigned in every exit path is assigned after the switch.

The verification harness checks string matching, multiple labels, single
evaluation, and nested loop control flow. Pattern labels and switch
expressions are outside this part of M6.
