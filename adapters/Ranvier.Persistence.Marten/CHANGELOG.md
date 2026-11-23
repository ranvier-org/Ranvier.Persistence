---
last_commit_released: d1fd751ad062179d661b7cb3c4f1647307633716
include:
  - ../../Directory.Build.props
depends_on:
  - ../../src/Ranvier.Persistence/
updaters:
  - xml:
      file: Ranvier.Persistence.Marten.fsproj
      selector: /Project/PropertyGroup/Version
---

# Changelog

This changelog is generated from conventional commits by [EasyBuild.ShipIt](https://github.com/easybuild-org/EasyBuild.ShipIt).

## 0.1.3 - 2026-11-23

### 🐞 Bug Fixes

* *(deps)* Align Ranvier core floors to 0.2.1 ([d1fd751](https://github.com/ranvier-org/Ranvier.Persistence/commit/d1fd751ad062179d661b7cb3c4f1647307633716))

### 🔗 Dependencies

* `src/Ranvier.Persistence` updated to 0.1.2

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Persistence/compare/c762ac8f7df6bae848f4837b2de77561053dbe7a..d1fd751ad062179d661b7cb3c4f1647307633716)</small></strong>

## 0.1.2 - 2026-11-16

### 🐞 Bug Fixes

* *(adapters)* Set the adapters' minimum FSharp.Core to 10.0.100 (#6) ([c762ac8](https://github.com/ranvier-org/Ranvier.Persistence/commit/c762ac8f7df6bae848f4837b2de77561053dbe7a))

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Persistence/compare/326cd231ca1907341207878b66c8f8052f674b9b..c762ac8f7df6bae848f4837b2de77561053dbe7a)</small></strong>

## 0.1.1 - 2026-11-14

### 🐞 Bug Fixes

* *(deps)* Build against Ranvier 0.2.0 (#3) ([326cd23](https://github.com/ranvier-org/Ranvier.Persistence/commit/326cd231ca1907341207878b66c8f8052f674b9b))

### 🔗 Dependencies

* `src/Ranvier.Persistence` updated to 0.1.1

<strong><small>[View changes on Github](https://github.com/ranvier-org/Ranvier.Persistence/compare/058ddf2c9a83d69faf8f1a2965eb25676d1cb4fb..326cd231ca1907341207878b66c8f8052f674b9b)</small></strong>

## 0.1.1-preview.1 - 2026-10-28

### Fixes

- Activate trusted publishing from the independent ranvier-org/Ranvier.Persistence repository and update the repository metadata.

## 0.1.0-preview.1
