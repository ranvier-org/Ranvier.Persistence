const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path'), zlib = require('node:zlib');
const { traceMarker, projectPackage, expectedPackages, zipEntries, dependencies, siblingVersionProblems, checkPackage, checkAnalyzerPackage, check } = require('./packages.cjs');

// A zip archive of the entries, the even-indexed ones deflated and the rest stored.
function zip(entries) {
  const locals = [], centrals = [];
  let offset = 0;
  Object.entries(entries).forEach(([name, content], index) => {
    const raw = Buffer.from(content);
    const method = index % 2 === 0 ? 8 : 0;
    const data = method === 8 ? zlib.deflateRawSync(raw) : raw;
    const fileName = Buffer.from(name);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(method, 8);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(raw.length, 22);
    local.writeUInt16LE(fileName.length, 26);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(method, 10);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(raw.length, 24);
    central.writeUInt16LE(fileName.length, 28);
    central.writeUInt32LE(offset, 42);
    locals.push(local, fileName, data);
    centrals.push(central, fileName);
    offset += local.length + fileName.length + data.length;
  });
  const directory = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(centrals.length / 2, 8);
  end.writeUInt16LE(centrals.length / 2, 10);
  end.writeUInt32LE(directory.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, directory, end]);
}

const project = `<Project><PropertyGroup>
  <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>
  <AssemblyName>Lib</AssemblyName>
  <PackageId>Lib</PackageId>
  <PackageId Condition="'$(RanvierTrace)' == 'true'">Lib.Traced</PackageId>
  <Version>0.1.0-preview.2</Version>
</PropertyGroup></Project>`;

function nupkg(id, { version = '0.1.0-preview.2', traced = id.endsWith('.Traced'), frameworks = ['net10.0', 'netstandard2.1'], dependencies = {} } = {}) {
  const dll = Buffer.concat([Buffer.from('MZ'), traced ? traceMarker : Buffer.from('RanvierTrace'), Buffer.from('end')]);
  const group = Object.entries(dependencies).map(([dependency, range]) => `<dependency id="${dependency}" version="${range}" exclude="Build,Analyzers" />`).join('');
  const entries = { [`${id}.nuspec`]: `<package><metadata><id>${id}</id><version>${version}</version><dependencies><group targetFramework="net10.0">${group}</group></dependencies></metadata></package>` };
  for (const framework of frameworks) entries[`lib/${framework}/Lib.dll`] = dll;
  entries['fable/Lib.fsproj'] = '<Project />';
  return zip(entries);
}

const lib = projectPackage(project, 'Lib');
const expected = expectedPackages([lib]);
const untraced = expected.get('Lib.0.1.0-preview.2.nupkg');
const traced = expected.get('Lib.Traced.0.1.0-preview.2.nupkg');

test('a project yields its untraced and traced packages at its version', () => {
  assert.deepEqual(lib, { id: 'Lib', version: '0.1.0-preview.2', assembly: 'Lib', frameworks: ['net10.0', 'netstandard2.1'], exactSiblings: false });
  assert.deepEqual([...expected.keys()], ['Lib.0.1.0-preview.2.nupkg', 'Lib.Traced.0.1.0-preview.2.nupkg']);
  assert.equal(untraced.traced, false);
  assert.equal(traced.traced, true);
});
test('zip entries read back stored and deflated', () => {
  const entries = zipEntries(zip({ 'a.txt': 'deflated', 'b/c.txt': 'stored' }));
  assert.equal(entries.get('a.txt')().toString(), 'deflated');
  assert.equal(entries.get('b/c.txt')().toString(), 'stored');
});
test('matching packages have no problems', () => {
  assert.deepEqual(checkPackage(nupkg('Lib'), untraced), []);
  assert.deepEqual(checkPackage(nupkg('Lib.Traced'), traced), []);
});
test('a traced id over an untraced build fails, and the reverse', () => {
  assert.deepEqual(checkPackage(nupkg('Lib.Traced', { traced: false }), traced), [
    'Lib.Traced: lib/net10.0/Lib.dll is untraced',
    'Lib.Traced: lib/netstandard2.1/Lib.dll is untraced',
  ]);
  assert.equal(checkPackage(nupkg('Lib', { traced: true }), untraced).length, 2);
});
test('a wrong id, version or framework set fails', () => {
  assert.deepEqual(checkPackage(nupkg('Other', { traced: false }), untraced), ['Lib: nuspec id is Other']);
  assert.deepEqual(checkPackage(nupkg('Lib', { version: '0.2.0' }), untraced), ['Lib: nuspec version is 0.2.0']);
  assert.deepEqual(checkPackage(nupkg('Lib', { frameworks: ['net10.0'] }), untraced), [
    'Lib: lib frameworks are [net10.0], expected [net10.0,netstandard2.1]',
    'Lib: missing lib/netstandard2.1/Lib.dll',
  ]);
});
test('a nuspec lists each dependency once with its range', () => {
  const entries = zipEntries(nupkg('Lib', { dependencies: { Core: '[1.0.0]', 'FSharp.Core': '8.0.100' } }));
  const manifest = entries.get('Lib.nuspec')().toString();
  assert.deepEqual([...dependencies(manifest)], [['Core', '[1.0.0]'], ['FSharp.Core', '8.0.100']]);
});
test('a package depends on the src packages of its own trace build', () => {
  const core = projectPackage(project.replace(/Lib/g, 'Core').replace('0.1.0-preview.2', '0.1.0-preview.6'), 'Core');
  assert.deepEqual(checkPackage(nupkg('Lib.Traced', { dependencies: { 'Core.Traced': '0.1.0-preview.6' } }), traced, [lib, core]), []);
  assert.deepEqual(checkPackage(nupkg('Lib.Traced', { dependencies: { Core: '0.1.0-preview.6' } }), traced, [lib, core]), [
    'Lib.Traced: depends on Core, expected Core.Traced',
  ]);
  assert.deepEqual(checkPackage(nupkg('Lib', { dependencies: { 'Core.Traced': '0.1.0-preview.6' } }), untraced, [lib, core]), [
    'Lib: depends on Core.Traced, expected Core',
  ]);
});
test('a package depends on the external packages of its own trace build', () => {
  assert.deepEqual(checkPackage(nupkg('Lib.Traced', { dependencies: { 'Ranvier.Traced': '0.1.1-preview.1' } }), traced, [lib], ['Ranvier']), []);
  assert.deepEqual(checkPackage(nupkg('Lib.Traced', { dependencies: { Ranvier: '0.1.1-preview.1' } }), traced, [lib], ['Ranvier']), [
    'Lib.Traced: depends on Ranvier, expected Ranvier.Traced',
  ]);
  assert.deepEqual(checkPackage(nupkg('Lib', { dependencies: { 'Ranvier.Traced': '0.1.1-preview.1' } }), untraced, [lib], ['Ranvier']), [
    'Lib: depends on Ranvier.Traced, expected Ranvier',
  ]);
});
const exactProject = (name, version) => projectPackage(project.replace(/Lib/g, name).replace('0.1.0-preview.2', version)
  .replace('</PropertyGroup>', '<RanvierExactSiblingVersions>true</RanvierExactSiblingVersions></PropertyGroup>'), name);
test('an exact sibling depends on each other exact sibling at exactly its version', () => {
  const exact = exactProject('Lib', '0.1.0-preview.2');
  const sibling = exactProject('Sibling', '0.1.0-preview.2');
  const core = projectPackage(project.replace(/Lib/g, 'Core').replace('0.1.0-preview.2', '0.1.0-preview.2'), 'Core');
  const pkg = expectedPackages([exact]).get('Lib.0.1.0-preview.2.nupkg');
  const projects = [exact, sibling, core];
  assert.deepEqual(checkPackage(nupkg('Lib', { dependencies: { Sibling: '[0.1.0-preview.2]', Core: '0.1.0-preview.2' } }), pkg, projects), []);
  assert.deepEqual(checkPackage(nupkg('Lib', { dependencies: { Sibling: '0.1.0-preview.2' } }), pkg, projects), [
    'Lib: depends on Sibling 0.1.0-preview.2, expected [0.1.0-preview.2]',
  ]);
  assert.deepEqual(checkPackage(nupkg('Lib', { dependencies: { Sibling: '0.1.0-preview.2' } }), untraced, projects), []);
});
test('a drifted exact sibling is still pinned at its own version', () => {
  const exact = exactProject('Lib', '0.1.0-preview.2');
  const sibling = exactProject('Sibling', '0.1.0-preview.3');
  const pkg = expectedPackages([exact]).get('Lib.0.1.0-preview.2.nupkg');
  assert.deepEqual(checkPackage(nupkg('Lib', { dependencies: { Sibling: '0.1.0-preview.3' } }), pkg, [exact, sibling]), [
    'Lib: depends on Sibling 0.1.0-preview.3, expected [0.1.0-preview.3]',
  ]);
});
test('exact-sibling projects must share one version', () => {
  const core = projectPackage(project.replace(/Lib/g, 'Core').replace('0.1.0-preview.2', '0.1.0-preview.6'), 'Core');
  assert.deepEqual(siblingVersionProblems([exactProject('Lib', '0.1.0-alpha.1'), exactProject('Sibling', '0.1.0-alpha.1'), core]), []);
  assert.deepEqual(siblingVersionProblems([exactProject('Lib', '0.1.0-alpha.1'), exactProject('Sibling', '0.1.0-alpha.2'), core]), [
    'exact-sibling projects have different versions: Lib 0.1.0-alpha.1, Sibling 0.1.0-alpha.2',
  ]);
});
test('a directory must hold exactly the expected packages', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'packages-'));
  const src = path.join(dir, 'src'), bin = path.join(dir, 'bin');
  try {
    fs.mkdirSync(path.join(src, 'Lib'), { recursive: true });
    fs.mkdirSync(bin);
    fs.writeFileSync(path.join(src, 'Lib', 'Lib.fsproj'), project);
    fs.writeFileSync(path.join(src, 'Directory.Build.props'), '<Project />');
    fs.mkdirSync(path.join(src, 'Tool'));
    fs.writeFileSync(path.join(src, 'Tool', 'Tool.fsproj'), '<Project><PropertyGroup><IsPackable>false</IsPackable><Version>0.1.0</Version></PropertyGroup></Project>');
    for (const id of ['Lib', 'Lib.Traced']) {
      fs.writeFileSync(path.join(bin, `${id}.0.1.0-preview.2.nupkg`), nupkg(id));
    }
    assert.deepEqual(check(bin, src), []);
    fs.writeFileSync(path.join(bin, 'Lib.0.0.9.nupkg'), nupkg('Lib', { version: '0.0.9' }));
    fs.rmSync(path.join(bin, 'Lib.Traced.0.1.0-preview.2.nupkg'));
    assert.deepEqual(check(bin, src), [
      'unexpected package Lib.0.0.9.nupkg',
      'missing package Lib.Traced.0.1.0-preview.2.nupkg',
    ]);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
test('an analyzer package holds its assembly under analyzers/dotnet/fs and its build assets', () => {
  const expected = { id: 'Ranvier.Analyzers', version: '0.1.0-preview.1', assembly: 'Ranvier.Analyzers', analyzer: true };
  const good = zip({
    'Ranvier.Analyzers.nuspec': '<package><metadata><id>Ranvier.Analyzers</id><version>0.1.0-preview.1</version></metadata></package>',
    'analyzers/dotnet/fs/Ranvier.Analyzers.dll': 'x',
    'build/Ranvier.Analyzers.props': '<Project />',
    'build/Ranvier.Analyzers.targets': '<Project />',
  });
  assert.deepEqual(checkAnalyzerPackage(good, expected), []);
  const withLib = zip({
    'Ranvier.Analyzers.nuspec': '<package><metadata><id>Ranvier.Analyzers</id><version>0.1.0-preview.1</version></metadata></package>',
    'lib/net10.0/Ranvier.Analyzers.dll': 'x',
  });
  assert.deepEqual(checkAnalyzerPackage(withLib, expected), [
    'Ranvier.Analyzers: has lib/ entries',
    'Ranvier.Analyzers: missing analyzers/dotnet/fs/Ranvier.Analyzers.dll',
    'Ranvier.Analyzers: missing build/Ranvier.Analyzers.props',
    'Ranvier.Analyzers: missing build/Ranvier.Analyzers.targets',
  ]);
});

test('additional adapter projects participate in the same package and dependency validation', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'adapter-packages-'));
  const src = path.join(dir, 'src'), bin = path.join(dir, 'bin');
  try {
    fs.mkdirSync(path.join(src, 'Lib'), { recursive: true });
    fs.mkdirSync(bin);
    fs.writeFileSync(path.join(src, 'Lib', 'Lib.fsproj'), project);
    const adapter = path.join(dir, 'Adapter.fsproj');
    fs.writeFileSync(adapter, project.replace(/>Lib(?=\.Traced<|<\/PackageId>)/g, '>Adapter'));
    for (const id of ['Lib', 'Lib.Traced', 'Adapter', 'Adapter.Traced']) {
      fs.writeFileSync(path.join(bin, `${id}.0.1.0-preview.2.nupkg`), nupkg(id));
    }
    assert.deepEqual(check(bin, src, [], [adapter]), []);
    fs.rmSync(path.join(bin, 'Adapter.Traced.0.1.0-preview.2.nupkg'));
    assert.deepEqual(check(bin, src, [], [adapter]), ['missing package Adapter.Traced.0.1.0-preview.2.nupkg']);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
