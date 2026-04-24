import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  reactStrictMode: true,
  trailingSlash: true,
  async rewrites() {
    return {
      beforeFiles: [
        {
          source: "/mirror/:path*",
          destination: "/mirror-static/:path*"
        }
      ]
    };
  },
  async redirects() {
    return [
      { source: "/en/login", destination: "/en/auth/login/", permanent: true },
      { source: "/fa/login", destination: "/fa/auth/login/", permanent: true },
      { source: "/en/register", destination: "/en/auth/register/", permanent: true },
      { source: "/fa/register", destination: "/fa/auth/register/", permanent: true }
    ];
  }
};

export default nextConfig;
