import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
const TOPIC_ALIASES = {
    templates: ["template", "templates", "production drawing", "production drawings", "hierarchy"],
    styles: ["style", "styles", "feature style", "label style", "code set"],
    layers: ["layer", "layers", "layer naming", "layer manager", "layer state"],
    labels: ["label", "labels", "labeling", "annotation", "annotate"],
    plotting: ["plot", "plotting", "stb", "ctb", "publish", "lineweight"],
    textstyles: ["textstyle", "textstyles", "font", "proposed", "existing"],
    proposed_existing: ["proposed", "existing", "design", "survey", "conditions"],
    pipe_networks: ["pipe", "pipes", "structure", "pressure pipe", "network"],
    profile_section: ["profile", "profiles", "section", "sections", "profile view", "section view"],
};
let cachedRulesPromise = null;
function getPromptRulesPath() {
    const currentDirectory = path.dirname(fileURLToPath(import.meta.url));
    return path.resolve(currentDirectory, "../standards/data/civil3d_framework_rules.json");
}
async function loadPromptRules() {
    if (!cachedRulesPromise) {
        cachedRulesPromise = readFile(getPromptRulesPath(), "utf8").then((raw) => {
            const parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed : [];
        });
    }
    return await cachedRulesPromise;
}
function normalizeText(value) {
    return value.toLowerCase().replace(/[^a-z0-9\s_\-]/g, " ").replace(/\s+/g, " ").trim();
}
function splitTerms(value) {
    return normalizeText(value)
        .split(" ")
        .map((term) => term.trim())
        .filter((term) => term.length >= 2);
}
function getTopicTerms(topic) {
    if (!topic) {
        return [];
    }
    const normalizedTopic = normalizeText(topic);
    return Array.from(new Set([normalizedTopic, ...(TOPIC_ALIASES[normalizedTopic] ?? [])].flatMap(splitTerms)));
}
function scoreRule(rule, args) {
    const queryTerms = splitTerms(args.query ?? "");
    const topicTerms = getTopicTerms(args.topic);
    const requestedTags = (args.tags ?? []).map(normalizeText);
    const searchableText = normalizeText(`${rule.title} ${rule.rule} ${rule.tags.join(" ")}`);
    let matchScore = 0;
    for (const tag of requestedTags) {
        if (rule.tags.map(normalizeText).includes(tag)) {
            matchScore += 12;
        }
    }
    for (const term of topicTerms) {
        if (searchableText.includes(term)) {
            matchScore += 6;
        }
    }
    for (const term of queryTerms) {
        if (searchableText.includes(term)) {
            matchScore += term.length >= 6 ? 5 : 3;
        }
    }
    if (args.topic && normalizeText(rule.title).includes(normalizeText(args.topic))) {
        matchScore += 8;
    }
    return matchScore + Math.min(rule.score, 20);
}
function buildRecommendedPractices(matches) {
    const uniqueRules = new Set();
    const recommendations = [];
    for (const match of matches) {
        const normalizedRule = match.rule.trim();
        if (!normalizedRule || uniqueRules.has(normalizedRule)) {
            continue;
        }
        uniqueRules.add(normalizedRule);
        recommendations.push(normalizedRule);
        if (recommendations.length >= 5) {
            break;
        }
    }
    return recommendations;
}
export async function lookupFrameworkStandards(args) {
    const rules = await loadPromptRules();
    const maxResults = Math.max(1, Math.min(args.maxResults ?? 5, 20));
    const scoredMatches = rules
        .map((rule) => ({
        rule,
        matchScore: scoreRule(rule, args),
    }))
        .filter(({ matchScore }) => {
        const hasFilter = Boolean(args.query?.trim()) || Boolean(args.topic?.trim()) || (args.tags?.length ?? 0) > 0;
        return hasFilter ? matchScore > 0 : true;
    })
        .sort((left, right) => right.matchScore - left.matchScore)
        .slice(0, maxResults)
        .map(({ rule, matchScore }) => ({
        sectionNumber: rule.section_number,
        title: rule.title,
        pages: rule.pages,
        rule: rule.rule,
        tags: rule.tags,
        sourceScore: rule.score,
        matchScore,
    }));
    return {
        query: args.query?.trim() || null,
        topic: args.topic?.trim() || null,
        tags: args.tags ?? [],
        totalRulesLoaded: rules.length,
        matchedCount: scoredMatches.length,
        matches: scoredMatches,
        recommendedPractices: buildRecommendedPractices(scoredMatches),
    };
}
