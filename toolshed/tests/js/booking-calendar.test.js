// Tests for the booking calendar's rules (no browser needed). The same cases are tested for the C# twin,
// DateSelection, in tests/ToolShed.Tests/DateSelectionTests.cs: the website and the app must agree.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createSelection, addDays, HORIZON_DAYS } = require('../../src/ToolShed.Web/wwwroot/js/booking-calendar.js');

const TODAY = '2026-05-01';                     // a Friday
const d = (n) => addDays(TODAY, n);
const make = (maxLoanDays = 14, held = []) =>
  createSelection({ today: TODAY, maxLoanDays, held: held.map(([s, e, approved]) => ({ start: d(s), end: d(e), approved: !!approved })) });

test('nothing is chosen to begin with', () => {
  const s = make();
  assert.equal(s.start, null);
  assert.equal(s.end, null);
  assert.equal(s.isComplete(), false);
  assert.equal(s.days(), 0);
  assert.match(s.summary(), /Pick the first day/);
});

test('the first day picked is always the start, the second is the end', () => {
  const s = make();
  assert.equal(s.pick(d(5)), true);
  assert.equal(s.start, d(5));
  assert.equal(s.end, null);
  assert.equal(s.pick(d(7)), true);
  assert.equal(s.start, d(5));
  assert.equal(s.end, d(7));
  assert.equal(s.days(), 3);
});

for (const earlier of [4, 1, 0]) {
  test(`day ${earlier} before the start cannot be picked as the end (no going backwards)`, () => {
    const s = make();
    s.pick(d(5));
    assert.equal(s.canPick(d(earlier)), false);
    assert.equal(s.pick(d(earlier)), false);
    assert.equal(s.start, d(5), 'the start did not move');
    assert.equal(s.end, null);
  });
}

test('however the clicks come, the end is never before the start and a booking is never under a day', () => {
  const s = make();
  let seed = 7;
  const rnd = (n) => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed % n; };
  for (let i = 0; i < 3000; i++) {
    s.pick(d(rnd(205) - 5));
    if (s.isComplete()) {
      assert.ok(s.end >= s.start, `end ${s.end} before start ${s.start}`);
      assert.ok(s.days() >= 1);
    }
  }
});

test('a single day booking is allowed by picking the same day twice, and counts as one day', () => {
  const s = make();
  s.pick(d(5));
  assert.equal(s.canPick(d(5)), true);
  assert.equal(s.pick(d(5)), true);
  assert.equal(s.days(), 1);
  assert.equal(s.summary(), 'Wed 6 May (1 day)');
});

test('picking again after a finished selection starts a new one, and clearing forgets both', () => {
  const s = make();
  s.pick(d(5));
  s.pick(d(7));
  assert.equal(s.pick(d(2)), true);
  assert.equal(s.start, d(2));
  assert.equal(s.end, null);
  s.pick(d(3));
  s.clear();
  assert.equal(s.start, null);
  assert.equal(s.end, null);
});

test('past days and days beyond the booking horizon cannot be picked', () => {
  const s = make();
  assert.equal(s.canPick(d(-1)), false);
  assert.equal(s.canPick(d(0)), true);
  assert.equal(s.canPick(d(HORIZON_DAYS)), true);
  assert.equal(s.canPick(d(HORIZON_DAYS + 1)), false);
});

test("the end cannot be further than the owner's longest loan", () => {
  const s = make(3);
  s.pick(d(10));
  assert.equal(s.canPick(d(12)), true);
  assert.equal(s.canPick(d(13)), false);
  assert.equal(s.latestEnd(), d(12));
});

test('a one day limit allows only the start day as the end', () => {
  const s = make(1);
  s.pick(d(4));
  assert.equal(s.canPick(d(4)), true);
  assert.equal(s.canPick(d(5)), false);
});

test('days already requested or booked cannot be picked as the start', () => {
  const s = make(14, [[5, 7]]);
  for (const n of [5, 6, 7]) assert.equal(s.canPick(d(n)), false);
  assert.equal(s.canPick(d(4)), true);
  assert.equal(s.canPick(d(8)), true);
});

test("the end cannot run across somebody else's booking, nor leap over it", () => {
  const s = make(14, [[8, 10]]);
  s.pick(d(5));
  assert.equal(s.canPick(d(7)), true);
  assert.equal(s.canPick(d(8)), false);
  assert.equal(s.canPick(d(11)), false);
  assert.equal(s.latestEnd(), d(7));
});

test('a start right before a booking can only be a single day', () => {
  const s = make(14, [[6, 9]]);
  s.pick(d(5));
  assert.equal(s.canPick(d(5)), true);
  assert.equal(s.canPick(d(6)), false);
  assert.equal(s.latestEnd(), d(5));
});

test('only the next booking after the start limits the end, whatever order they are given in', () => {
  const s = make(30, [[20, 22], [3, 4], [12, 14]]);
  s.pick(d(6));
  assert.equal(s.latestEnd(), d(11));
  assert.equal(s.canPick(d(13)), false);
  assert.equal(s.canPick(d(21)), false);
});

test('a booking before the start does not limit the end', () => {
  const s = make(5, [[1, 3]]);
  s.pick(d(5));
  assert.equal(s.latestEnd(), d(9));
});

test('a picked selection never overlaps a held range or exceeds the limit', () => {
  const s = make(10, [[8, 10], [15, 16]]);
  let seed = 11;
  const rnd = (n) => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed % n; };
  for (let i = 0; i < 3000; i++) {
    s.pick(d(rnd(40)));
    if (!s.isComplete()) continue;
    for (let n = 0; n < s.days(); n++) {
      assert.equal(s.heldRangeAt(addDays(s.start, n)), null, `${s.start} to ${s.end} overlaps a held range`);
    }
    assert.ok(s.days() <= 10);
  }
});

test('the prompt tells you what to do next', () => {
  const s = make();
  s.pick(d(5));
  assert.match(s.summary(), /Now pick the last day/);
  assert.match(s.summary(), /Earlier days cannot be chosen/);
  s.pick(d(8));
  assert.equal(s.summary(), 'Wed 6 May to Sat 9 May (4 days)');
});

test('date arithmetic survives month, year and leap-day boundaries and daylight saving', () => {
  assert.equal(addDays('2026-01-31', 1), '2026-02-01');
  assert.equal(addDays('2026-12-31', 1), '2027-01-01');
  assert.equal(addDays('2028-02-28', 1), '2028-02-29');
  assert.equal(addDays('2026-03-28', 2), '2026-03-30');   // across a European clock change
  assert.equal(addDays('2026-11-01', 1), '2026-11-02');   // across a US clock change
  assert.equal(addDays('2026-05-01', -1), '2026-04-30');
});
