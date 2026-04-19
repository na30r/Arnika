export default function HomePage() {
  return (
    <main>
      <div className="card">
        <h1>Web Mirror Platform</h1>
        <p>
          Mirrored pages are served under <code>/mirror/&lt;domain&gt;/...&lt;path&gt;</code>.
        </p>
        <p>
          Example route: <code>/mirror/example.com/docs/getting-started</code>
        </p>
      </div>
    </main>
  );
}
