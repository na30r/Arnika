import { notFound } from 'next/navigation';
import { readMirroredPage } from '@/lib/mirror-storage';

type MirrorPageProps = {
  params: Promise<{ slug: string[] }>;
};

export default async function MirrorPage({ params }: MirrorPageProps) {
  const { slug = [] } = await params;

  try {
    const { html } = await readMirroredPage(slug);
    return <div dangerouslySetInnerHTML={{ __html: html }} />;
  } catch {
    notFound();
  }
}
