module Halo4::HomeHelper
	def resolve_asset_url(asset, size = 'large')
		asset_url = asset['assetUrl'].gsub('{size}', size.to_s)
		base_url = asset['baseUrl']

		public_path = "games/halo4/#{base_url}/#{asset_url}"

		case base_url
		when 'H4MapAssets'
			public_path = public_path.gsub('.png', '.jpg')
		end

		if public_asset_exists?(public_path)
			puts public_path
			puts asset
			return image_url(public_path)
		end

		url = Halo4Client.instance.metadata_options['settings'][base_url]
		return url + asset_url
	end

	def resolve_spartan_image(gamertag, pose = 'fullbody', size = 'large')
		return "https://spartans.svc.halowaypoint.com/players/#{gamertag}/h4/spartans/#{pose}?target=#{size}"
	end
end
