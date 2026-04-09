# Change Log

All notable changes to this project will be documented in this file. See [versionize](https://github.com/versionize/versionize) for commit guidelines.

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

