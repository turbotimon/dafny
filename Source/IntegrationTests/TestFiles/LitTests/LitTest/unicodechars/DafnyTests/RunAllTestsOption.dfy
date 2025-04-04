// RUN: %verify --unicode-char --verify-included-files "%s" > "%t"
// RUN: ! %baredafny test %args --unicode-char --no-verify --target:cs "%s" >> "%t"
// RUN: ! %baredafny test %args --unicode-char --no-verify --target:java "%s" >> "%t"
// RUN: ! %baredafny test %args --unicode-char --no-verify --target:go "%s" >> "%t"
// RUN: ! %baredafny test %args --unicode-char --no-verify --target:js "%s" >> "%t"
// RUN: ! %baredafny test %args --unicode-char --no-verify --target:py "%s" >> "%t"
// RUN: %diff "%s.expect" "%t" 
include "../../DafnyTests/RunAllTestsOption.dfy"
