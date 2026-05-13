# Change Log

All notable changes to this project will be documented in this file. See [versionize](https://github.com/versionize/versionize) for commit guidelines.

<a name="2.8.2"></a>
## [2.8.2](https://www.github.com/framlux/vord/releases/tag/v2.8.2) (2026-05-12)

### Bug Fixes

* fixing npm and go version pinning ([3c74395](https://www.github.com/framlux/vord/commit/3c74395d3ee72128d5f6923fc6d16b2d412e51b9))

<a name="2.8.1"></a>
## [2.8.1](https://www.github.com/framlux/vord/releases/tag/v2.8.1) (2026-05-12)

### Bug Fixes

* Fixing code standard violations and broken temp nuget feed ([ee72b36](https://www.github.com/framlux/vord/commit/ee72b36e8845363108dcc732423ef09431dea065))

<a name="2.8.0"></a>
## [2.8.0](https://www.github.com/framlux/vord/releases/tag/v2.8.0) (2026-05-12)

### Features

* Add ReportMachineUsageAsync unit tests for billing client ([df4eb4e](https://www.github.com/framlux/vord/commit/df4eb4e0b3cddb51019f9fc75b7b2e8270961b38))
* Add TierFeatureLimits and TenantSubscriptionOverrides tables ([38d5a71](https://www.github.com/framlux/vord/commit/38d5a71d76aa43aa1a5ae0442f0c4c02cfc09f8c))
* Add UsageHeartbeatService unit tests ([f7158b1](https://www.github.com/framlux/vord/commit/f7158b18d436d900316dd63348f2fc0b50b75aa1))
* Adding historical lookup of telemtry ([7786fc0](https://www.github.com/framlux/vord/commit/7786fc082db750033eab889676accbbb5abc16fe))
* Alert delivery decoupling, webhook secrets, rule lifecycle, auth guards, and MachineOffline metric ([9886c56](https://www.github.com/framlux/vord/commit/9886c56fe213cf7656fcef5f95470e1ad5f0f495))
* Frontend fixes, test quality improvements, and defensive bug fixes ([b739181](https://www.github.com/framlux/vord/commit/b7391819fc95c4cecfd01a17a865701c24a41452))
* Implement metered billing in fleet server ([869b715](https://www.github.com/framlux/vord/commit/869b7150b7e9e1edc0967fd8f4bfbd2b309e0f36))
* Overhauling subscriptions to be metered billing for more fair pricing ([254384b](https://www.github.com/framlux/vord/commit/254384b0b266a7e29039ad9af10887ae54c1f0c9))
* Overhauling Webhooks and adding Teams / Slack / PagerDuty integrations ([b9f01e4](https://www.github.com/framlux/vord/commit/b9f01e4ce435582d76072b09328769aa3ae55c8a))
* Pass pending action to billing-api on cancel/downgrade ([c39cef3](https://www.github.com/framlux/vord/commit/c39cef38e18c67daa59ac887abe8c4c8c3c450f1))
* Splitting server out to API server and service worker, with test splits ([59961b7](https://www.github.com/framlux/vord/commit/59961b72bdf9c0719639cb3759beebcba8dbc4ac))
* Upgrading Alerts to be machine-specific ([bdc45f6](https://www.github.com/framlux/vord/commit/bdc45f644fdbaf2b8bde57773543c46180eceffc))

### Bug Fixes

* Add cross-cutting test coverage for alerts pre-production readiness ([0782780](https://www.github.com/framlux/vord/commit/0782780261d66597b2a037d0aac18d7d07fd16ff))
* Adding more tests ([33f3940](https://www.github.com/framlux/vord/commit/33f3940ebd3b1023899c8c102a9ceacf57d21a8f))
* Adding more tests ([7387c21](https://www.github.com/framlux/vord/commit/7387c21eadcaf3e5462dc63b47e59fa99a25c678))
* Claude code-review fixes ([68a5067](https://www.github.com/framlux/vord/commit/68a5067dcddb84a65330fd9a54e04df9d14305db))
* Eliminate flaky test caused by concurrent FastEndpoints host creation ([824f85d](https://www.github.com/framlux/vord/commit/824f85d7d16b896a0088e4c8557e757c0b9434a8))
* Fixing billing integrations ([8e70b6b](https://www.github.com/framlux/vord/commit/8e70b6ba4dcd1e2e358419dc249b22c8b96fdf6a))
* Fixing broken alerts ([38be340](https://www.github.com/framlux/vord/commit/38be340311b094086c309221da3df2ffe5b3cac6))
* Fixing broken install script and Dashboard vs Details page mismatch ([f5e2391](https://www.github.com/framlux/vord/commit/f5e23911ad445316b2ca01ba484fdb6b254c44ac))
* Fixing Claude-code-review found horizontal scalability issues ([21eaeea](https://www.github.com/framlux/vord/commit/21eaeea80923b3cdb369ec7bb6843a46f59447d2))
* Fixing code review issues in Svelte found by Claude ([8f8d62d](https://www.github.com/framlux/vord/commit/8f8d62d1b6f224d7b3dfa430cc7fed6d10ef2854))
* Fixing code review issues via Claude ([29a7599](https://www.github.com/framlux/vord/commit/29a7599f40caa2ba1e46206e73e975e11164ed52))
* Fixing FastEndpoint Validator<T> pattern testing ([8b9886a](https://www.github.com/framlux/vord/commit/8b9886ad30791061d984d00e60ea979e93bfa137))
* fixing Go agent from Claude code review feedback ([619853a](https://www.github.com/framlux/vord/commit/619853a6cfb1b30f6792b7f475edd177e9f70838))
* Fixing incorrect assert usage for newer TUnit ([4938dbc](https://www.github.com/framlux/vord/commit/4938dbca50e941dff0bc3d5364f84d45e240937e))
* Fixing test warnings and removing project reference for nuget reference ([7788f01](https://www.github.com/framlux/vord/commit/7788f0155bb0cafa7cdc4b48691d63a00d4a1a6f))
* Moving DB query layer to a Repository pattern for better maintenance ([5cffca7](https://www.github.com/framlux/vord/commit/5cffca75f6f4c5d80d6f92f30edb45c12cf822d5))
* moving to FastEndpoints native DTO validation ([d4fcbd6](https://www.github.com/framlux/vord/commit/d4fcbd61d523f3982e7e1a1783878992f633617e))
* Schema and model fixes for alerts pre-production readiness ([0a3bc59](https://www.github.com/framlux/vord/commit/0a3bc59093262deda0a33663636e6ce908f384b0))
* Updating Alerts to be Metric or Event based for better customization ([af8aef8](https://www.github.com/framlux/vord/commit/af8aef829e85e123326e857ca8acfeda434b67fe))

<a name="2.7.0"></a>
## [2.7.0](https://www.github.com/framlux/vord/releases/tag/v2.7.0) (2026-04-27)

### Features

* Optimizing agent and server storage space per-machine ([3f18294](https://www.github.com/framlux/vord/commit/3f182948235dd5fcd5c805c7399e93dd569a213e))

### Bug Fixes

* Fixing accessibility and responsiveness ([b50523c](https://www.github.com/framlux/vord/commit/b50523c94c852ec1c81982f1fae7a42f8a40e6d4))

<a name="2.6.3"></a>
## [2.6.3](https://www.github.com/framlux/vord/releases/tag/v2.6.3) (2026-04-22)

### Bug Fixes

* Fixing broken CD build spec ([7814ca5](https://www.github.com/framlux/vord/commit/7814ca541adf73ed5205ac43de6e2d0213689efb))

<a name="2.6.2"></a>
## [2.6.2](https://www.github.com/framlux/vord/releases/tag/v2.6.2) (2026-04-22)

### Bug Fixes

* Fixing broken icon and build type check miss ([04b1363](https://www.github.com/framlux/vord/commit/04b1363a12738c0dcb7a25e07c5b22a76652cbaa))

<a name="2.6.1"></a>
## [2.6.1](https://www.github.com/framlux/vord/releases/tag/v2.6.1) (2026-04-22)

### Bug Fixes

* Fixing broken support link in production testing ([1285de3](https://www.github.com/framlux/vord/commit/1285de3b34e403a2fa36680b0cbdfb0f818e4af5))

<a name="2.6.0"></a>
## [2.6.0](https://www.github.com/framlux/vord/releases/tag/v2.6.0) (2026-04-22)

### Features

* Adding links to the support page ([87877b0](https://www.github.com/framlux/vord/commit/87877b00015275490cd9690d84f516bba28c2fad))

<a name="2.5.1"></a>
## [2.5.1](https://www.github.com/framlux/vord/releases/tag/v2.5.1) (2026-04-22)

### Bug Fixes

* Fixing missed machine name updates ([c6b5d8e](https://www.github.com/framlux/vord/commit/c6b5d8e3bce5099c9878824f73b7b6b29af72565))

<a name="2.5.0"></a>
## [2.5.0](https://www.github.com/framlux/vord/releases/tag/v2.5.0) (2026-04-21)

### Features

* Adding edit for machine Name, Description, and Location ([c3d55f5](https://www.github.com/framlux/vord/commit/c3d55f557d4599a39316b4499a8c04f7d52b74a1))

### Bug Fixes

* bumping C# package versions ([c078f16](https://www.github.com/framlux/vord/commit/c078f166e4ad9af2b9878a6b38f2a2c0a7b61c4f))

<a name="2.4.0"></a>
## [2.4.0](https://www.github.com/framlux/vord/releases/tag/v2.4.0) (2026-04-21)

### Features

* Upgrading the navigation to be more clear and usable ([aaa9a20](https://www.github.com/framlux/vord/commit/aaa9a20ffad11fd92aa7ffb03cd58f48968210b8))

<a name="2.3.1"></a>
## [2.3.1](https://www.github.com/framlux/vord/releases/tag/v2.3.1) (2026-04-19)

### Bug Fixes

* Fixing race condition in functional test initialization ([22b6c84](https://www.github.com/framlux/vord/commit/22b6c8461db13959c6b7aa66b34a086a8ee44621))

<a name="2.3.0"></a>
## [2.3.0](https://www.github.com/framlux/vord/releases/tag/v2.3.0) (2026-04-19)

### Features

* Adding agent capabilities flag to turn UI on or off based on agent configuration ([55eff8f](https://www.github.com/framlux/vord/commit/55eff8f9624f0e94096983a19836423da1af61c3))
* Removing old Certificates for API keys and updating Remote Command certs ([f7da694](https://www.github.com/framlux/vord/commit/f7da69439efa2a22c69e8b3cf5df2e7edd30f46d))

### Bug Fixes

* Removing debug UI Telemetry tab and corresponding APIs ([73a6c44](https://www.github.com/framlux/vord/commit/73a6c44bdb256ed0c39c3435be42a222c10e5d54))

<a name="2.2.2"></a>
## [2.2.2](https://www.github.com/framlux/vord/releases/tag/v2.2.2) (2026-04-13)

### Bug Fixes

* Fixing broken JSON serializing casing ([f6d9976](https://www.github.com/framlux/vord/commit/f6d99764c63d08e136e67f0183995b9666f2668f))
* Fixing missing null-guards on machine details page ([d54917c](https://www.github.com/framlux/vord/commit/d54917c36e48a78b5937796142cace79fc32034a))

<a name="2.2.1"></a>
## [2.2.1](https://www.github.com/framlux/vord/releases/tag/v2.2.1) (2026-04-12)

### Bug Fixes

* Fixing broken partitioning algorithm and ensuring we only take in valid telemetry ([6e2d8cb](https://www.github.com/framlux/vord/commit/6e2d8cb27fcd67ed296b99c100baeb09c66e15b4))
* Removing 2 tests that were tautologies ([3dbb0ba](https://www.github.com/framlux/vord/commit/3dbb0baecd28b3a3c40ae202338dabbfdbc05cef))

<a name="2.2.0"></a>
## [2.2.0](https://www.github.com/framlux/vord/releases/tag/v2.2.0) (2026-04-12)

### Features

* MOving from monthly postgres partitions to daily for better DB performance ([b096ad8](https://www.github.com/framlux/vord/commit/b096ad86d126261c44a936d57938560445f90b51))

<a name="2.1.3"></a>
## [2.1.3](https://www.github.com/framlux/vord/releases/tag/v2.1.3) (2026-04-12)

### Bug Fixes

* Reducing log spam from API, fixing SSH telemetry config to server driven ([dbb5f75](https://www.github.com/framlux/vord/commit/dbb5f759564218bbc26e5c9120a8d952ccd790cd))

<a name="2.1.2"></a>
## [2.1.2](https://www.github.com/framlux/vord/releases/tag/v2.1.2) (2026-04-12)

### Bug Fixes

* Fixing broken machine API key flow; fixing broken UI when no telemetry exists ([6b4f2ea](https://www.github.com/framlux/vord/commit/6b4f2ea0d53122bfc1913d655128b8fbd4cee8a2))

<a name="2.1.1"></a>
## [2.1.1](https://www.github.com/framlux/vord/releases/tag/v2.1.1) (2026-04-12)

### Bug Fixes

* Fixing mismatched jsonb column declarations and SSH script for machine install ([89b17c7](https://www.github.com/framlux/vord/commit/89b17c7711834d90cfdea3e2cafd3a68a05f5158))

<a name="2.1.0"></a>
## [2.1.0](https://www.github.com/framlux/vord/releases/tag/v2.1.0) (2026-04-09)

### Features

* Adding more unit and functional tests ([be8925d](https://www.github.com/framlux/vord/commit/be8925dfc37fc18da05c8b13b82cc9e39cd62ea1))

### Bug Fixes

* Fixing security issues found via Claude security audit ([15345ea](https://www.github.com/framlux/vord/commit/15345ea806c7ead30fb911fb34d615655fb036c5))

<a name="2.0.4"></a>
## [2.0.4](https://www.github.com/framlux/vord/releases/tag/v2.0.4) (2026-04-06)

### Bug Fixes

* Moving machine registration under Machines and fixing permissions ([498bff7](https://www.github.com/framlux/vord/commit/498bff7fce40fae6a43ba90425af75a58557b385))

<a name="2.0.3"></a>
## [2.0.3](https://www.github.com/framlux/vord/releases/tag/v2.0.3) (2026-04-05)

### Bug Fixes

* Fixing broken DB audit log Linq2Db type ([c6f9833](https://www.github.com/framlux/vord/commit/c6f9833221f4c84dcf99fa4078020061b1dc127d))

<a name="2.0.2"></a>
## [2.0.2](https://www.github.com/framlux/vord/releases/tag/v2.0.2) (2026-04-05)

### Bug Fixes

* Fixing broken DB inserts that violated FK constraints ([b4019e8](https://www.github.com/framlux/vord/commit/b4019e8972b71587206da2d1a089ed2298b9a43f))

<a name="2.0.1"></a>
## [2.0.1](https://www.github.com/framlux/vord/releases/tag/v2.0.1) (2026-04-05)

### Bug Fixes

* Fixing incorrect IfDatabase key in migration schema ([4b2eb7a](https://www.github.com/framlux/vord/commit/4b2eb7ae2e4c5369b9ae22ab18671bf387c76039))

<a name="2.0.0"></a>
## [2.0.0](https://www.github.com/framlux/vord/releases/tag/v2.0.0) (2026-04-05)

### Features

* adding machine search, removing full table scans for search, and fixing missing data retention ([9ce9b55](https://www.github.com/framlux/vord/commit/9ce9b5598060feed263d1d04c91e95bd011a8beb))
* Removing upserts and moving hot-path to insert-only ([42b5d37](https://www.github.com/framlux/vord/commit/42b5d37c1a93cf756336ecefe61d1cdfe170cdca))
* Unifying DB migrations (with better partitioning) before customers join ([100a0cd](https://www.github.com/framlux/vord/commit/100a0cd5e388d11f00ddbd8ea54b4f6c9b0aa0fb))

### Breaking Changes

* Unifying DB migrations (with better partitioning) before customers join ([100a0cd](https://www.github.com/framlux/vord/commit/100a0cd5e388d11f00ddbd8ea54b4f6c9b0aa0fb))

<a name="1.5.3"></a>
## [1.5.3](https://www.github.com/framlux/vord/releases/tag/v1.5.3) (2026-04-02)

### Bug Fixes

* Fixing JSON serialization to standardize and fixing tenant names to remove special character injection ([6cd6b69](https://www.github.com/framlux/vord/commit/6cd6b69ea4876ce526bee9b926ccd33c1e776b2e))

<a name="1.5.2"></a>
## [1.5.2](https://www.github.com/framlux/vord/releases/tag/v1.5.2) (2026-04-02)

### Bug Fixes

* Fixing broken API contracts and incorrect navigation highlighting ([696fb6a](https://www.github.com/framlux/vord/commit/696fb6a7e45b344f1402509dff9f43e88c270fd1))

<a name="1.5.1"></a>
## [1.5.1](https://www.github.com/framlux/vord/releases/tag/v1.5.1) (2026-03-31)

### Bug Fixes

* Fixing multi-tenant authZ bug where we would not set the tenant cookie ([8736db0](https://www.github.com/framlux/vord/commit/8736db024aa3615e46d2d9ea09217aa1e71c706a))

<a name="1.5.0"></a>
## [1.5.0](https://www.github.com/framlux/vord/releases/tag/v1.5.0) (2026-03-31)

### Features

* Adding Server Configuration management and updating agent configuration ([4daec05](https://www.github.com/framlux/vord/commit/4daec055fe33dcb48988bc7f5e0c2e4554db3b1b))

<a name="1.4.5"></a>
## [1.4.5](https://www.github.com/framlux/vord/releases/tag/v1.4.5) (2026-03-30)

### Bug Fixes

* branches were all messed up, fixing ([78d7be5](https://www.github.com/framlux/vord/commit/78d7be57a91f43ad726232de82e38dc3acd77169))
* Fixing broken functional test ([fb7a0c1](https://www.github.com/framlux/vord/commit/fb7a0c11eaa1bae8e8d7742e81d67c8a669e9edb))

