# AWS serverless deployment target

Guito (expense-tracking API + webapp) is being revived and deployed to AWS, deliberately, to learn AWS for work. The API runs as .NET 8 Lambda functions behind an API Gateway HTTP API with logs in CloudWatch (replacing the old App Service hosting and the Serilog Azure Blob sink); the UI is a static SPA served from S3 + CloudFront. Cost must stay near zero — AWS free tier only.

**Consequences**: personal tool; no 24/7 warm instances. The AWS agent role `arn:aws:iam::497087877832:role/MinervaAIAgent` is used for all AWS operations, never the user identity.
