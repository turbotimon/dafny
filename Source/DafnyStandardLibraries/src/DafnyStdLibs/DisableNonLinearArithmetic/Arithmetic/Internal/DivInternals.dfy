/*******************************************************************************
 *  Original: Copyright (c) Microsoft Corporation
 *  SPDX-License-Identifier: MIT
 *  
 *  Modifications and Extensions: Copyright by the contributors to the Dafny Project
 *  SPDX-License-Identifier: MIT 
 *******************************************************************************/

/* lemmas and functions in this file are used in the proofs in DivMod.dfy 

Specs/implements mathematical div and mod, not the C version.
(x div n) * n + (x mod n) == x, where 0 <= x mod n < n.
https://en.wikipedia.org/wiki/Modulo_operation

This may produce "surprising" results for negative values.
For example, -3 div 5 is -1 and -3 mod 5 is 2.
Note this is consistent: -3 * -1 + 2 == 5 */

module DafnyStdLibs.Arithmetic.DivInternals {

  import opened GeneralInternals
  import opened ModInternals
  import opened ModInternalsNonlinear
  import opened DivInternalsNonlinear
  import opened MulInternals

  /* Performs division recursively with positive denominator. */
  function {:opaque} DivPos(x: int, d: int): int
    requires d > 0
    decreases if x < 0 then (d - x) else x
  {
    if x < 0 then
      -1 + DivPos(x + d, d)
    else if x < d then
      0
    else
      1 + DivPos(x - d, d)
  }

  /* Performs division recursively. */
  function {:opaque} DivRecursive(x: int, d: int): int
    requires d != 0
  {
    reveal DivPos();
    if d > 0 then
      DivPos(x, d)
    else
      -1 * DivPos(x, -1 * d)
  }

  /* Proves the basics of the division operation */
  lemma LemmaDivBasics(n: int)
    requires n > 0
    ensures  n / n == -((-n) / n) == 1
    ensures  forall x:int {:trigger x / n} :: 0 <= x < n <==> x / n == 0
    ensures  forall x:int {:trigger (x + n) / n} :: (x + n) / n == x / n + 1
    ensures  forall x:int {:trigger (x - n) / n} :: (x - n) / n == x / n - 1
  {
    LemmaModAuto(n);
    LemmaModBasics(n);
    LemmaSmallDiv();
    LemmaDivBySelf(n);
    forall x: int | x / n == 0
      ensures 0 <= x < n
    {
      LemmaFundamentalDivMod(x, n);
    }
  }

  /* Automates the division operator process. Contains the identity property, a
  fact about when quotients are zero, and facts about adding and subtracting
  integers over a common denominator. */
  ghost predicate DivAuto(n: int)
    requires n > 0
  {
    && ModAuto(n)
    && (n / n == -((-n) / n) == 1)
    && (forall x: int {:trigger x / n} :: 0 <= x < n <==> x / n == 0)
    && (forall x: int, y: int {:trigger (x + y) / n} ::
          (var z := (x % n) + (y % n);
           ((0 <= z < n && (x + y) / n == x / n + y / n) ||
            (n <= z < n + n && (x + y) / n == x / n + y / n + 1))))
    && (forall x: int, y: int {:trigger (x - y) / n} ::
          (var z := (x % n) - (y % n);
           ((0 <= z < n && (x - y) / n == x / n - y / n) ||
            (-n <= z < 0 && (x - y) / n == x / n - y / n - 1))))
  }

  lemma {:vcs_split_on_every_assert} /*{:rlimit 3014}*/ LemmaDivAutoAux(n: int)
    requires n > 0 && ModAuto(n)
    ensures DivAuto(n)
  {
    LemmaDivBasics(n);
    assert (0 + n) / n == 1;
    assert (0 - n) / n == -1;
    forall x:int, y:int {:trigger (x + y) / n}
      ensures  var z := (x % n) + (y % n);
               (|| (0 <= z < n && (x + y) / n == x / n + y / n)
                || (n <= z < 2 * n && (x + y) / n == x / n + y / n + 1))
    {
      var f := (xx:int, yy:int) =>
          (var z := (xx % n) + (yy % n);
           (   (0 <= z < n && (xx + yy) / n == xx / n + yy / n)
               || (n <= z < 2 * n && (xx + yy) / n == xx / n + yy / n + 1)));
      forall i, j
        ensures j >= 0 && f(i, j) ==> f(i, j + n)
        ensures i < n  && f(i, j) ==> f(i - n, j)
        ensures j < n  && f(i, j) ==> f(i, j - n)
        ensures i >= 0 && f(i, j) ==> f(i + n, j)
      {
        assert ((i + n) + j) / n == ((i + j) + n) / n;
        assert (i + (j + n)) / n == ((i + j) + n) / n;
        assert ((i - n) + j) / n == ((i + j) - n) / n;
        assert (i + (j - n)) / n == ((i + j) - n) / n;
      }
      forall i, j
        ensures 0 <= i < n && 0 <= j < n ==> f(i, j)
      {
        assert ((i + n) + j) / n == ((i + j) + n) / n;
        assert (i + (j + n)) / n == ((i + j) + n) / n;
        assert ((i - n) + j) / n == ((i + j) - n) / n;
        assert (i + (j - n)) / n == ((i + j) - n) / n;
      }
      LemmaModInductionForall2(n, f);
      assert f(x, y);
    }
    forall x:int, y:int {:trigger (x - y) / n}
      ensures  var z := (x % n) - (y % n);
               (|| (0 <= z < n && (x - y) / n == x / n - y / n)
                || (-n <= z < 0 && (x - y) / n == x / n - y / n - 1))
    {
      var f := (xx:int, yy:int) =>
          (var z := (xx % n) - (yy % n);
           (   (0 <= z < n && (xx - yy) / n == xx / n - yy / n)
               || (-n <= z < 0 && (xx - yy) / n == xx / n - yy / n - 1)));
      forall i, j
        ensures j >= 0 && f(i, j) ==> f(i, j + n)
        ensures i < n  && f(i, j) ==> f(i - n, j)
        ensures j < n  && f(i, j) ==> f(i, j - n)
        ensures i >= 0 && f(i, j) ==> f(i + n, j)
      {
        assert ((i + n) - j) / n == ((i - j) + n) / n;
        assert (i - (j - n)) / n == ((i - j) + n) / n;
        assert ((i - n) - j) / n == ((i - j) - n) / n;
        assert (i - (j + n)) / n == ((i - j) - n) / n;
      }
      forall i, j
        ensures 0 <= i < n && 0 <= j < n ==> f(i, j)
      {
        assert ((i + n) - j) / n == ((i - j) + n) / n;
        assert (i - (j - n)) / n == ((i - j) + n) / n;
        assert ((i - n) - j) / n == ((i - j) - n) / n;
        assert (i - (j + n)) / n == ((i - j) - n) / n;
      }
      LemmaModInductionForall2(n, f);
      assert f(x, y);
    }
  }

  /* Ensures that DivAuto is true */
  lemma LemmaDivAuto(n: int)
    requires n > 0
    ensures  DivAuto(n)
  {
    LemmaModAuto(n);
    LemmaDivAutoAux(n);
  }

  /* Performs auto induction for division */
  lemma LemmaDivInductionAuto(n: int, x: int, f: int->bool)
    requires n > 0
    requires DivAuto(n) ==> && (forall i {:trigger IsLe(0, i)} :: IsLe(0, i) && i < n ==> f(i))
                            && (forall i {:trigger IsLe(0, i)} :: IsLe(0, i) && f(i) ==> f(i + n))
                            && (forall i {:trigger IsLe(i + 1, n)} :: IsLe(i + 1, n) && f(i) ==> f(i - n))
    ensures  DivAuto(n)
    ensures  f(x)
  {
    LemmaDivAuto(n);
    assert forall i :: IsLe(0, i) && i < n ==> f(i);
    assert forall i {:trigger f(i), f(i + n)} :: IsLe(0, i) && f(i) ==> f(i + n);
    assert forall i {:trigger f(i), f(i - n)} :: IsLe(i + 1, n) && f(i) ==> f(i - n);
    LemmaModInductionForall(n, f);
    assert f(x);
  }

  /* Performs auto induction on division for all i s.t. f(i) exists */
  lemma LemmaDivInductionAutoForall(n:int, f:int->bool)
    requires n > 0
    requires DivAuto(n) ==> && (forall i {:trigger IsLe(0, i)} :: IsLe(0, i) && i < n ==> f(i))
                            && (forall i {:trigger IsLe(0, i)} :: IsLe(0, i) && f(i) ==> f(i + n))
                            && (forall i {:trigger IsLe(i + 1, n)} :: IsLe(i + 1, n) && f(i) ==> f(i - n))
    ensures  DivAuto(n)
    ensures  forall i {:trigger f(i)} :: f(i)
  {
    LemmaDivAuto(n);
    assert forall i :: IsLe(0, i) && i < n ==> f(i);
    assert forall i {:trigger f(i), f(i + n)} :: IsLe(0, i) && f(i) ==> f(i + n);
    assert forall i {:trigger f(i), f(i - n)} :: IsLe(i + 1, n) && f(i) ==> f(i - n);
    LemmaModInductionForall(n, f);
  }

}
