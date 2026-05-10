# TODO

## Infrastructure

- **Upgrade Node.js to v22 LTS** — pnpm 11 requires `node:sqlite` which isn't available in Node 20. Currently `pnpm build` fails but `npx vite build` works as a workaround. Upgrade to Node 22 LTS to fix the pnpm wrapper.

