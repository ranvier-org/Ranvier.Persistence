// The packages `build.fsx -- publish` pushes: for each src/<Name>/<Name>.fsproj, `<PackageId>` and `<PackageId>.Traced`
// at the project's `<Version>`, each with a lib/<tfm>/<AssemblyName>.dll per `<TargetFrameworks>` entry. A traced package
// depends on the traced packages of other src projects and an untraced one on their untraced packages. The projects
// with `<RanvierExactSiblingVersions>true</RanvierExactSiblingVersions>` share one version and depend on each other at
// exactly that version.
// Each analyzer project passed on the command line packs once, with its assembly under analyzers/dotnet/fs.
// Each package id passed after --external, such as the core Ranvier packages, follows the same traced/untraced rule
// as a src project when a package depends on it.
const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');

// The AssemblyMetadata("RanvierTrace", "true") blob: two length-prefixed UTF-8 strings. Only a traced build carries it.
const traceMarker = Buffer.concat([Buffer.from([12]), Buffer.from('RanvierTrace'), Buffer.from([4]), Buffer.from('true')]);

function property(xml, name) {
  const match = new RegExp(`<${name}>\\s*([^<]+?)\\s*</${name}>`).exec(xml);
  return match ? match[1] : null;
}

// The untraced package of a project file; the unconditional `<PackageId>` is the untraced id.
function projectPackage(xml, name) {
  const frameworks = property(xml, 'TargetFrameworks') ?? property(xml, 'TargetFramework');
  return {
    id: property(xml, 'PackageId') ?? name,
    version: property(xml, 'Version'),
    assembly: property(xml, 'AssemblyName') ?? name,
    frameworks: (frameworks ?? '').split(';').map(f => f.trim()).filter(Boolean).sort(),
    exactSiblings: property(xml, 'RanvierExactSiblingVersions') === 'true',
  };
}

// Every package publish pushes, untraced and traced, keyed by its .nupkg file name.
function expectedPackages(projects) {
  const packages = new Map();
  for (const project of projects) {
    for (const traced of [false, true]) {
      const id = traced ? `${project.id}.Traced` : project.id;
      packages.set(`${id}.${project.version}.nupkg`, { ...project, id, traced });
    }
  }
  return packages;
}

// The entries of a zip archive, by name, each read on demand. Stored and deflated entries only, as NuGet writes.
function zipEntries(buffer) {
  let end = buffer.length - 22;
  while (end >= 0 && buffer.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw new Error('not a zip archive');
  const entries = new Map();
  let at = buffer.readUInt32LE(end + 16);
  for (let i = buffer.readUInt16LE(end + 10); i > 0; i--) {
    const method = buffer.readUInt16LE(at + 10);
    const size = buffer.readUInt32LE(at + 20);
    const nameLength = buffer.readUInt16LE(at + 28);
    const local = buffer.readUInt32LE(at + 42);
    const name = buffer.toString('utf8', at + 46, at + 46 + nameLength);
    entries.set(name, () => {
      const start = local + 30 + buffer.readUInt16LE(local + 26) + buffer.readUInt16LE(local + 28);
      const data = buffer.subarray(start, start + size);
      return method === 8 ? zlib.inflateRawSync(data) : data;
    });
    at += 46 + nameLength + buffer.readUInt16LE(at + 30) + buffer.readUInt16LE(at + 32);
  }
  return entries;
}

// The id and version range of each `<dependency>` of a nuspec, across its framework groups.
function dependencies(manifest) {
  const found = new Map();
  for (const [element] of manifest.matchAll(/<dependency\b[^>]*>/g)) {
    const id = /\bid="([^"]+)"/.exec(element);
    const version = /\bversion="([^"]+)"/.exec(element);
    if (id) found.set(id[1], version ? version[1] : null);
  }
  return found;
}

// The problems of one .nupkg against its expected package and the src projects it may depend on; empty when it matches.
function checkPackage(buffer, expected, projects = [], external = []) {
  const problems = [];
  const entries = zipEntries(buffer);
  const nuspec = [...entries.keys()].find(name => !name.includes('/') && name.endsWith('.nuspec'));
  if (!nuspec) return [`${expected.id}: no .nuspec`];
  const manifest = entries.get(nuspec)().toString('utf8');
  if (property(manifest, 'id') !== expected.id) problems.push(`${expected.id}: nuspec id is ${property(manifest, 'id')}`);
  if (property(manifest, 'version') !== expected.version) problems.push(`${expected.id}: nuspec version is ${property(manifest, 'version')}`);
  const libs = [...entries.keys()].filter(name => name.startsWith('lib/'));
  const frameworks = [...new Set(libs.map(name => name.split('/')[1]))].sort();
  if (frameworks.join(';') !== expected.frameworks.join(';')) {
    problems.push(`${expected.id}: lib frameworks are [${frameworks}], expected [${expected.frameworks}]`);
  }
  for (const [id, version] of dependencies(manifest)) {
    const base = id.endsWith('.Traced') ? id.slice(0, -'.Traced'.length) : id;
    const sibling = projects.find(project => project.id === base);
    if (!sibling && !external.includes(base)) continue;
    const wanted = expected.traced ? `${base}.Traced` : base;
    if (id !== wanted) problems.push(`${expected.id}: depends on ${id}, expected ${wanted}`);
    if (sibling && expected.exactSiblings && sibling.exactSiblings && version !== `[${sibling.version}]`) {
      problems.push(`${expected.id}: depends on ${id} ${version}, expected [${sibling.version}]`);
    }
  }
  for (const framework of expected.frameworks) {
    const dll = `lib/${framework}/${expected.assembly}.dll`;
    if (!entries.has(dll)) {
      problems.push(`${expected.id}: missing ${dll}`);
    } else if (entries.get(dll)().includes(traceMarker) !== expected.traced) {
      problems.push(`${expected.id}: ${dll} is ${expected.traced ? 'untraced' : 'traced'}`);
    }
  }
  return problems;
}

// The problems of an analyzer .nupkg: no lib/ entries, the assembly under analyzers/dotnet/fs, and the build assets.
function checkAnalyzerPackage(buffer, expected) {
  const problems = [];
  const entries = zipEntries(buffer);
  const nuspec = [...entries.keys()].find(name => !name.includes('/') && name.endsWith('.nuspec'));
  if (!nuspec) return [`${expected.id}: no .nuspec`];
  const manifest = entries.get(nuspec)().toString('utf8');
  if (property(manifest, 'id') !== expected.id) problems.push(`${expected.id}: nuspec id is ${property(manifest, 'id')}`);
  if (property(manifest, 'version') !== expected.version) problems.push(`${expected.id}: nuspec version is ${property(manifest, 'version')}`);
  if ([...entries.keys()].some(name => name.startsWith('lib/'))) problems.push(`${expected.id}: has lib/ entries`);
  for (const file of [`analyzers/dotnet/fs/${expected.assembly}.dll`, `build/${expected.id}.props`, `build/${expected.id}.targets`]) {
    if (!entries.has(file)) problems.push(`${expected.id}: missing ${file}`);
  }
  return problems;
}

// One problem when the exact-sibling projects declare more than one version, listing each project's version.
function siblingVersionProblems(projects) {
  const siblings = projects.filter(project => project.exactSiblings);
  if (new Set(siblings.map(project => project.version)).size <= 1) return [];
  return [`exact-sibling projects have different versions: ${siblings.map(project => `${project.id} ${project.version}`).join(', ')}`];
}

// The problems of a package directory against the packable projects under src and the analyzer projects; empty when
// it holds exactly the expected packages.
function check(packagesDirectory, sourceDirectory, analyzerProjects = [], additionalProjects = [], external = []) {
  const projects = fs.readdirSync(sourceDirectory, { withFileTypes: true })
    .filter(entry => entry.isDirectory() && fs.existsSync(path.join(sourceDirectory, entry.name, `${entry.name}.fsproj`)))
    .map(entry => ({ name: entry.name, xml: fs.readFileSync(path.join(sourceDirectory, entry.name, `${entry.name}.fsproj`), 'utf8') }))
    .filter(({ xml }) => property(xml, 'IsPackable') !== 'false')
    .map(({ name, xml }) => projectPackage(xml, name));
  for (const file of additionalProjects) {
    const xml = fs.readFileSync(file, 'utf8');
    if (property(xml, 'IsPackable') !== 'false') projects.push(projectPackage(xml, path.basename(file, '.fsproj')));
  }
  if (projects.length === 0) return [`no project under ${sourceDirectory}`];
  const problems = siblingVersionProblems(projects);
  const expected = expectedPackages(projects);
  const analyzers = new Map(analyzerProjects.map(file => {
    const pkg = { ...projectPackage(fs.readFileSync(file, 'utf8'), path.basename(file, '.fsproj')), analyzer: true };
    return [`${pkg.id}.${pkg.version}.nupkg`, pkg];
  }));
  const files = fs.existsSync(packagesDirectory) ? fs.readdirSync(packagesDirectory) : [];
  for (const file of files.filter(f => f.endsWith('.nupkg') && !expected.has(f) && !analyzers.has(f))) problems.push(`unexpected package ${file}`);
  for (const [file, pkg] of expected) {
    if (!files.includes(file)) {
      problems.push(`missing package ${file}`);
      continue;
    }
    problems.push(...checkPackage(fs.readFileSync(path.join(packagesDirectory, file)), pkg, projects, external));
  }
  for (const [file, pkg] of analyzers) {
    if (!files.includes(file)) problems.push(`missing package ${file}`);
    else problems.push(...checkAnalyzerPackage(fs.readFileSync(path.join(packagesDirectory, file)), pkg));
  }
  return problems;
}

module.exports = { traceMarker, projectPackage, expectedPackages, zipEntries, dependencies, siblingVersionProblems, checkPackage, checkAnalyzerPackage, check };

if (require.main === module) {
// node packages.cjs <packages directory> <src directory> [analyzer .fsproj ...] [--libraries adapter .fsproj ...]
//   [--external package id ...]
  const [packagesDirectory = 'bin', sourceDirectory = 'src', ...args] = process.argv.slice(2);
  const externalAt = args.indexOf('--external');
  const external = externalAt < 0 ? [] : args.slice(externalAt + 1);
  const rest = externalAt < 0 ? args : args.slice(0, externalAt);
  const separator = rest.indexOf('--libraries');
  const analyzerProjects = separator < 0 ? rest : rest.slice(0, separator);
  const additionalProjects = separator < 0 ? [] : rest.slice(separator + 1);
  const problems = check(packagesDirectory, sourceDirectory, analyzerProjects, additionalProjects, external);
  for (const problem of problems) console.error(problem);
  if (problems.length > 0) process.exit(1);
  console.log(`Packages in ${packagesDirectory} match the projects in ${sourceDirectory}.`);
}
