using AutoMapper;
using StudentService.Domain.DTOs;
using StudentService.Infrastructure.Models;

namespace StudentService.Infrastructure.Profiles
{
	public class StudentMappingProfile : Profile
	{
		public StudentMappingProfile() 
		{
			CreateMap<Student, StudentBasicInfo>()
				.ForMember(dest => dest.StudentId, opt => opt.MapFrom(src => src.StudentId.ToString()))
				.ForMember(dest => dest.FirstName, opt => opt.MapFrom(src => src.FirstName))
				.ForMember(dest => dest.LastName, opt => opt.MapFrom(src => src.LastName))
				.ForMember(dest => dest.Rating, opt => opt.MapFrom(src => CalculateRating(src)));

			CreateMap<Student, StudentDetailInfo>()
				.ForMember(dest => dest.StudentId, opt => opt.MapFrom(src => src.StudentId.ToString()))
				.ForMember(dest => dest.FirstName, opt => opt.MapFrom(src => src.FirstName))
				.ForMember(dest => dest.LastName, opt => opt.MapFrom(src => src.LastName))
				.ForMember(dest => dest.Rating, opt => opt.MapFrom(src => CalculateRating(src)));

			CreateMap<Trait, TraitInfo>();
			CreateMap<JobTypeHistory, JobTypeHistoryInfo>();
		}

		private static double? CalculateRating(Student src)
		{
			if (src.ThumbsReceived == null)
				return null;

			var total = src.ThumbsReceived.Up + src.ThumbsReceived.Down;
			if (total == 0)
				return null;

			var ratio = src.ThumbsReceived.Up == 0 ? 1.0 : (double)src.ThumbsReceived.Up / total;
			return Math.Round(ratio * 5, 2);
		}
	}
}
