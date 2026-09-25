# web (Angular 22)

Requires Node 24.15+ (or 22.22.3+).

```bash
npm ci
npm start          # http://localhost:4200, calls the API at public/config.json -> apiBaseUrl
npm test -- --watch=false
npm run build      # output: dist/web/browser
```

`public/config.json` is read at startup, so the same build works in every environment;
the CD pipeline overwrites it with the Azure API URL.
