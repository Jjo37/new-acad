export type HelpTopicType =
  | "Concept"
  | "Procedure"
  | "Command Reference"
  | "Dialog Reference"
  | "Troubleshooting"
  | "Tutorial"
  | "Reference";

export interface HelpInstallation {
  root: string;
  version: string;
  language: string;
  displayName: string;
  configured: boolean;
}

export interface HelpImageRef {
  id: string;
  uri: string;
  path: string;
  alt: string;
  caption: string;
  mimeType: string;
  width?: number;
  height?: number;
  size: number;
}

export interface HelpVideoRef {
  id: string;
  uri: string;
  title: string;
  sourceVersion: string;
  pageUrl: string;
  mp4Url: string;
  webmUrl?: string;
}

export interface HelpTopicIndexEntry {
  id: string;
  topicId: string;
  uri: string;
  title: string;
  summary: string;
  version: string;
  product: string;
  featureArea: string;
  topicType: HelpTopicType;
  headings: string[];
  tags: string[];
  relatedTopicIds: string[];
  images: HelpImageRef[];
  sourcePath: string;
  canonicalUrl?: string;
  contentHash: string;
  searchText: string;
}

export interface HelpTopic extends HelpTopicIndexEntry {
  markdown: string;
}

export interface HelpSearchResult {
  id: string;
  uri: string;
  title: string;
  summary: string;
  excerpt: string;
  version: string;
  featureArea: string;
  topicType: HelpTopicType;
  tags: string[];
  canonicalUrl?: string;
  imageCount: number;
  score: number;
}

export interface HelpIndexStatus {
  version: string;
  root: string;
  topicCount: number;
  imageCount: number;
  cachePath: string;
  sourceFingerprint: string;
  indexedAt: string;
  loadedFromCache: boolean;
}

export interface HelpIndexCache {
  schemaVersion: 1;
  version: string;
  root: string;
  sourceFingerprint: string;
  indexedAt: string;
  topics: HelpTopicIndexEntry[];
}
