const test = require('node:test');
const assert = require('node:assert/strict');
const { headerProblem, check } = require('./contrib.cjs');

test('accepts conventional titles with optional scopes and breaking markers', () => {
  for (const title of ['feat: add an API', 'fix(core): handle null', 'feat!: remove an API', 'perf(fable/core): reduce allocations']) {
    assert.equal(headerProblem(title), null, title);
  }
});

test('rejects titles that ShipIt cannot classify', () => {
  for (const title of ['Update stuff', 'feat:no space', 'Feat: uppercase', 'feature: unknown type', 'fix(): empty scope', 'fix:  ', '', undefined]) {
    assert.notEqual(headerProblem(title), null, String(title));
  }
});

test('reports the contributor guide for invalid human titles and exempts bots', async () => {
  const failures = [];
  const core = { setFailed: message => failures.push(message), notice: () => {} };
  const context = { payload: { pull_request: { title: 'Update stuff', user: { type: 'User', login: 'contributor' } } } };
  await check({ context, core });
  assert.equal(failures.length, 1);
  assert.match(failures[0], /CONTRIBUTOR\.md#commit-messages/);
  context.payload.pull_request.user.type = 'Bot';
  failures.length = 0;
  await check({ context, core });
  assert.deepEqual(failures, []);
});
