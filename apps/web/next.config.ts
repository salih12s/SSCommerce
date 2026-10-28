import type { NextConfig } from "next";

// Same-origin proxy (DECISIONS D-006): tarayıcı yalnızca Next.js origin'ine istek atar.
const apiBaseUrl = process.env.API_BASE_URL ?? "http://localhost:5080";

const nextConfig: NextConfig = {
  cacheComponents: true,
  partialPrefetching: true,
  reactCompiler: true,
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
  async rewrites() {
    return [
      {
        source: "/api/backend/:path*",
        destination: `${apiBaseUrl}/:path*`,
      },
    ];
  },
};

export default nextConfig;
